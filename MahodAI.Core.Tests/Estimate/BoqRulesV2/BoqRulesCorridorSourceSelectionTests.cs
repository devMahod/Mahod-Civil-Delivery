using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using R = MahodAI.CivilDelivery.Estimate.BoqRulesV2.BoqRulesSourceResolver;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Review 01/10: an experiment copy measured later in the same profile silently replaced the estimate's corridor source.
/// The corridor source is now one drawing — chosen explicitly when the profile has several — and its own latest measurement
/// only; a missing choice or an invalid chosen latest is a typed refusal (no workbook), never another drawing or an older run.
/// Codex's 20 isolated checks (CODEX_SelectionTests.cs 8010CF7C, 20/20 on the candidate), ported verbatim into this lane.
/// </summary>
public sealed class BoqRulesCorridorSourceSelectionTests
{
    private static void Run(Action<Fixture> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "boq-corridor-selection-" + Guid.NewGuid().ToString("N"));
        try { check(new Fixture(root)); }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static void Must(bool value, string why) { if (!value) throw new Exception(why); }

    private static void Refused(Action action, params string[] messages)
    {
        try { action(); throw new Exception("Expected typed refusal, but resolution returned (writer could proceed)."); }
        catch (R.CorridorSourceSelectionException ex)
        {
            foreach (var part in messages) Must(ex.Message.Contains(part, StringComparison.Ordinal), $"Missing refusal reason: {part}. Actual: {ex.Message}");
        }
    }

    [Fact]
    public void S00_no_runs_is_none() => Run(f =>
    {
            var r = f.Resolve(); Must(r.Input == null && r.Evidence.Count == 0 && r.Notes.Single().Contains("לא נמצאה"), "None disclosure lost");
            Must(R.ListCorridorSources(f.Runs,"6422").Count == 0, "Invented choice");
    });

    [Fact]
    public void S01_single_source_disclosed_and_partial_allowed() => Run(f =>
    {
            f.Publish("a1", f.A, 1); var r=f.Resolve();
            Must(r.Input?.RunId=="a1" && r.Notes.Single().Contains("היחיד") && r.Input.Export.Measures.Count==2,"Single partial valid fixture must work");
    });

    [Fact]
    public void S02_two_sources_no_implicit_latest() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("b2",f.B,2); Refused(()=>f.Resolve(),"יש לבחור");
    });

    [Fact]
    public void S03_approved_a_wins_over_newer_experiment_b() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("b2",f.B,2);
            var hashed=new List<string>(); var r=R.ResolveCorridor(f.Runs,"6422",f.Rules,f.Sha,f.A,p=>{hashed.Add(p);return ArtifactHash.Sha256OfFile(p);});
            Must(r.Input?.RunId=="a1" && r.Evidence[0].Path==f.A && hashed.SequenceEqual(new[]{f.A}),"Wrong source or unrelated source read");
            // Presentation 3B103650 (Codex 12:37): the note names the chosen file (LTR-wrapped); the full path stays in Evidence.
            Must(r.Notes.Single().Contains("במפורש") && r.Notes.Single().Contains(Bidi.Ltr(Path.GetFileName(f.A))) &&
                 !r.Notes.Single().Contains(f.A) && r.Evidence[0].Path==f.A,"Selection not disclosed");
    });

    [Fact]
    public void S04_explicit_experiment_b_also_works() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("b2",f.B,2); Must(f.Resolve(f.B).Input?.RunId=="b2","B explicit selection must work");
    });

    [Fact]
    public void S05_latest_inside_selected_source() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("a2",f.A,2); f.Publish("b3",f.B,3);
            Must(f.Resolve(f.A).Input?.RunId=="a2","Not latest A"); var options=R.ListCorridorSources(f.Runs,"6422");
            Must(options.Count==2 && options[0].DrawingPath==f.B && options[1].RunCount==2 && options[1].LatestRunId=="a2","Inventory/count/order incorrect");
    });

    [Fact]
    public void S06_path_case_and_dot_normalization() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("a2",Path.Combine(Path.GetDirectoryName(f.A)!,".","A.dwg"),2);
            Must(R.ListCorridorSources(f.Runs,"6422").Count==1,"Equivalent path split");
            Must(f.Resolve(f.A.ToUpperInvariant()).Input?.RunId=="a2","Normalized case selection failed");
    });

    [Fact]
    public void S07_changed_selected_source_refuses_without_other_fallback() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("b2",f.B,2); File.AppendAllText(f.A,"changed");
            Refused(()=>f.Resolve(f.A),"a1","השתנה מאז המדידה");
    });

    [Fact]
    public void S08_fresh_measurement_after_same_path_save_succeeds() => Run(f =>
    {
            f.Publish("a1",f.A,1); File.AppendAllText(f.A,"new revision"); f.Publish("a3",f.A,3); f.Publish("b4",f.B,4);
            Must(f.Resolve(f.A).Input?.RunId=="a3","Fresh same source measurement blocked");
    });

    [Fact]
    public void S09_selected_source_absent_or_wrong_profile_refuses() => Run(f =>
    {
            f.Publish("b1",f.B,1); f.Publish("a2",f.A,2,profile:"7000");
            Refused(()=>f.Resolve(f.A),"לא נמצאה","6422");
    });

    [Fact]
    public void S10_latest_selected_missing_measures_no_old_fallback() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("a2",f.A,2,measures:false); f.Publish("b3",f.B,3);
            Refused(()=>f.Resolve(f.A),"a2","לא שמרה קובץ מדידה");
            Must(R.ListCorridorSources(f.Runs,"6422").Single(x=>x.DrawingPath==f.A).LatestRunId=="a2","Invalid latest hidden from options");
    });

    [Fact]
    public void S11_invalid_selected_path_never_none() => Run(f =>
    {
            Refused(()=>f.Resolve("relative.dwg"),"נתיב מלא");
            Refused(()=>f.Resolve(""),"נתיב מלא");
            Refused(()=>f.Resolve(f.A),"לא נמצאה");
    });

    [Fact]
    public void S12_changed_raw_or_measures_preserve_guards() => Run(f =>
    {
            var a=f.Publish("a1",f.A,1); File.AppendAllText(Path.Combine(a,Fixture.Raw)," ");
            Refused(()=>f.Resolve(f.A),"נתוני הגלם");
            a=f.Publish("a2",f.A,2); File.AppendAllText(Path.Combine(a,CorridorBoqMeasuresFile.FileName)," ");
            Refused(()=>f.Resolve(f.A),"השתנה מאז שפורסם");
    });

    [Fact]
    public void S13_rules_and_header_profile_preserve_guards() => Run(f =>
    {
            f.Publish("a1",f.A,1,rulesSha:new string('0',64)); Refused(()=>f.Resolve(f.A),"בכללי קורידורים");
            f.Publish("a2",f.A,2,headerProfile:"7000"); Refused(()=>f.Resolve(f.A),"שייך לפרופיל");
    });

    [Fact]
    public void S14_failed_latest_and_missing_drawing_refuse() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("a2",f.A,2,status:DeliveryStatus.Failed);
            Refused(()=>f.Resolve(f.A),"a2","מצב הריצה");
            f.Publish("a3",f.A,3); File.Delete(f.A); Refused(()=>f.Resolve(f.A),"אינו נגיש כעת");
    });

    [Fact]
    public void S15_reviewrequired_valid_draft_accepted() => Run(f =>
    {
            f.Publish("a1",f.A,1,status:DeliveryStatus.ReviewRequired); Must(f.Resolve(f.A).Input?.RunId=="a1","Valid draft refused");
    });

    [Fact]
    public void S16_other_profile_and_pending_do_not_create_choices() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("b2",f.B,2,profile:"7000"); f.Publish(".pending-b3",f.B,3);
            Must(R.ListCorridorSources(f.Runs,"6422").Count==1 && f.Resolve().Input?.RunId=="a1","Profile/pending guard changed");
    });

    [Fact]
    public void S17_same_selected_snapshot_recheck_does_not_switch_sources() => Run(f =>
    {
            f.Publish("a1",f.A,1); var first=f.Resolve(f.A); f.Publish("b2",f.B,2); var after=f.Resolve(f.A);
            Must(first.Input?.RunId==after.Input?.RunId && first.Evidence.SequenceEqual(after.Evidence),"Recheck switched to B");
            File.AppendAllText(f.A,"save during export"); Refused(()=>f.Resolve(f.A),"השתנה מאז המדידה");
    });

    [Fact]
    public void S18_resolution_is_readonly_and_selection_roundtrip() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("b2",f.B,2);
            var before=Directory.GetFiles(f.Root,"*",SearchOption.AllDirectories).ToDictionary(x=>x,ArtifactHash.Sha256OfFile);
            var selection=JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(f.A)); f.Resolve(selection);
            var after=Directory.GetFiles(f.Root,"*",SearchOption.AllDirectories).ToDictionary(x=>x,ArtifactHash.Sha256OfFile);
            Must(before.Count==after.Count && before.All(x=>after[x.Key]==x.Value),"Resolver wrote source/receipt");
    });

    [Fact]
    public void S19_same_source_latest_timestamp_tie_refuses() => Run(f =>
    {
            f.Publish("a1",f.A,1); f.Publish("a2",f.A,1); f.Publish("b3",f.B,3);
            Refused(()=>f.Resolve(f.A),"אותו זמן סיום");
            Must(f.Resolve(f.B).Input?.RunId=="b3","Tie in unrelated source blocked selected B");
    });

    private sealed class Fixture
    {
        public const string Raw="corridor_boq_raw_inputs.json";
        public string Root{get;} public string Runs=>Path.Combine(Root,"runs");
        public string A=>Path.Combine(Root,"drawings","A.dwg");
        public string B=>Path.Combine(Root,"drawings","B.dwg");
        public CorridorBoqRuleset Rules{get;}=CorridorBoqRuleset.LoadEmbedded6422();
        public string Sha{get;}=CorridorBoqMeasuresFile.EmbeddedRulesetSha256();
        public Fixture(string root) { Root=root;Directory.CreateDirectory(Runs);Directory.CreateDirectory(Path.GetDirectoryName(A)!);File.WriteAllText(A,"approved source");File.WriteAllText(B,"diagnostic source");}
        public R.CorridorResolution Resolve(string? selected=null)=>R.ResolveCorridor(Runs,"6422",Rules,Sha,selected);
        public string Publish(string id,string drawing,int hour,string profile="6422",bool measures=true,string? rulesSha=null,
            DeliveryStatus status=DeliveryStatus.Blocked,string? headerProfile=null)
        {
            var dir=Path.Combine(Runs,id);Directory.CreateDirectory(dir);var raw=Path.Combine(dir,Raw);
            File.WriteAllText(raw,"{\"schema\":\"mahod-corridor-boq-raw/1\",\"run\":\""+id+"\"}");
            var drawingHash=ArtifactHash.Sha256OfFile(drawing);
            var manifest=new RunManifest { RunId=id,Feature="estimate",Operation=R.CorridorOperation,
                CompletedAtUtc=new DateTime(2026,10,1,hour,0,0,DateTimeKind.Utc),ProjectProfileId=profile,ResultStatus=status,
                InputDrawings={drawing},InputHashesByPath={[drawing]=drawingHash},ArtifactHashes={[raw]=ArtifactHash.Sha256OfFile(raw)} };
            if(measures)
            {
                var node=JsonNode.Parse(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("corridor_measures_r9_west.json")))!;
                var h=node["Header"]!;h["RulesetSha256"]=rulesSha??Sha;h["DrawingPath"]=drawing;h["DrawingSha256"]=drawingHash;
                h["RawReceiptFile"]=Raw;h["RawReceiptSha256"]=ArtifactHash.Sha256OfFile(raw);h["ProfileId"]=headerProfile??profile;
                var path=Path.Combine(dir,CorridorBoqMeasuresFile.FileName);File.WriteAllText(path,node.ToJsonString());
                manifest.ArtifactHashes[path]=ArtifactHash.Sha256OfFile(path);
            }
            File.WriteAllText(Path.Combine(dir,"run_manifest.json"),JsonSerializer.Serialize(manifest,RunManifestWriter.JsonOptions));
            return dir;
        }
    }
}
