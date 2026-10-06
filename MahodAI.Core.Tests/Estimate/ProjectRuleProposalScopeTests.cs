using System.Text.Json;
using System.Text.Json.Nodes;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using C = MahodAI.CivilDelivery.Estimate.ProjectRuleRecordContext;
using P = MahodAI.CivilDelivery.Estimate.ProjectRuleProposalScope;
using FluentAssertions;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// L05 scope adapter (Codex 04647DC5, integrated unchanged): the focused checks of its isolated harness, run against the
/// live Core. Eligibility admits only a consistent GenericUnchanged group; Pending, mixed, stale and rule-governed groups
/// never reach the rankers. The harness pins of the frozen Core/NTS assemblies are dropped — this links the product build.
/// </summary>
public sealed class ProjectRuleProposalScopeTests
{
    [Fact]
    public void TheIsolatedHarnessChecksHoldOnTheProductCore()
    {

        var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); }
        void Throws(Action action, string name) { try { action(); } catch (ArgumentException) { passed.Add(name); return; } throw new Exception(name); }
        string H(char c) => new(c,64);
        NeutralQuantityRecord N(string id, string layer = "KERB", string key = "GROUP", string project = "PROJECT", string kind = "length", string unit = "m") => new()
        {
            RecordId=id, ProjectProfileId=project, RunId="run",
            Source=new QuantitySource { Drawing="test-A-model.dwg", DrawingPath="C:/local/test-A-model.dwg", DrawingHash=H('a'), Handle=id, Layer=layer, EntityType="LINE" },
            Measurement=new QuantityMeasurement { Kind=kind, Unit=unit, Method="line-length", RawValue=3, Parameters=new() },
            Classification=new QuantityClassification { RuleKey=key },
        };
        C.Request Fixture(NeutralQuantityRecord[] records)
        {
            const string rulesText="""
            {"schema":"mahod-boq-rules/2","project":"PROJECT","version":"1","existing_layers_prefix":"OLD_",
             "parameters":[{"id":"road_class","label":"road","value":1}],
             "chapters":[{"id":"10.01","title":"test"}],"source_roles":[{"id":"A","file_pattern":"-A-"}],
             "boq":[{"id":"K1","chapter":"10.01","item":"10.01.0010","unit":"m","confirm":"נדרש אישור הנדסי","review":"בדיקה בלבד",
                      "parts":[{"label":"אבן שפה","src":["A"],"layers":["KERB"],"kind":"length","object_width_m":0.3}]},
                     {"id":"K2","chapter":"10.01","item":"10.01.0020","unit":"m","parts":[{"label":"שפה אחרת","src":["A"],"layers":["EDGE"],"kind":"length"}]}]}
            """;
            var rules=BoqRuleset.Parse(rulesText);
            var scan=new BoqNeutralRecordAdapter.SourceScan("A","run","C:/local/test-A-model.dwg",H('a'),records,Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") };
            var identity=new C.SourceIdentity("A","run",scan.DrawingPath,scan.DrawingHash,H('b'),C.RecordDigest(records),H('c'),C.AuxiliaryDigest(scan));
            var sources=new[]{new C.Source(identity,scan)};
            var result=BoqRulesEngine.Run(rules,BoqNeutralRecordAdapter.Build(rules,new[]{scan}));
            var stamp=new C.Stamp(records.FirstOrDefault()?.ProjectProfileId??"PROJECT","run",scan.DrawingPath,scan.DrawingHash,
                H('e'),H('f'),H('1'),rules.Sha256,"catalog",H('2'),C.SourceSetDigest(sources.Select(s=>s.Identity)),H('4'),C.OutcomeDigest(result));
            return new(stamp,stamp,sources,records,result);
        }
        (C.Request request,P.Prepared prepared,P.Evaluation evaluation) Run(params NeutralQuantityRecord[] records)
        {
            var f=Fixture(records); var p=P.Capture(C.Bind(f),records); return(f,p,P.Evaluate(p,f.Current,records));
        }
        bool NoEligible(P.Evaluation e) => e.Groups.All(g=>!g.GenericProposalEligible && !g.IsApproval && !g.IsPrice);

        // 1: actual frozen adapter + engine + ED1, useful guidance and generic preservation.
        var positive=Run(N("1"),N("2"));
        var group=positive.evaluation.Groups.Single();
        Check(group.State==P.Disposition.RuleReview && group.MemberIds.SequenceEqual(new[]{"1","2"}),"positive consistent full group");
        Check(group.Guidance.All(g=>g.LineId=="K1"&&g.RuleCatalogCode=="10.01.0010"&&g.RuleUnit=="m"&&g.ObjectWidth==.3),"actual rule line code unit width guidance retained");
        Check(group.Guidance.All(g=>g.Confirmation=="נדרש אישור הנדסי"&&!g.IsApproval&&!g.IsPrice),"explicit confirmation not approval");
        Check(NoEligible(positive.evaluation)&&group.Message.Contains("הסכום הגולמי"),"derived review not actionable raw mapping");
        var generic=Run(N("1",project:"OTHER"),N("2",project:"OTHER"));
        Check(generic.evaluation.ContextMatches&&generic.evaluation.Groups.Single().GenericProposalEligible,"non-6422 generic unchanged useful positive");
        Check(!generic.evaluation.Groups.Single().IsApproval&&!generic.evaluation.Groups.Single().IsPrice,"generic eligibility is not manual-review approval; existing scope/save guards remain external");
        Check(generic.evaluation.Groups.Single().Guidance.All(g=>g.State==C.State.GenericUnchanged&&g.RuleCatalogCode==null),"no rule code leaks to generic context");
        var approvedRecord=N("2",project:"OTHER"); approvedRecord.Classification.CandidateCatalogCode="EXISTING";
        var approved=Run(N("1",project:"OTHER"),approvedRecord);
        Check(approved.evaluation.Groups.Single().MemberIds.Count==2&&approved.evaluation.Groups.Single().UnmappedMemberIds.SequenceEqual(new[]{"1"}),"approved member conserved but not candidate member");
        Check(approvedRecord.Classification.CandidateCatalogCode=="EXISTING"&&approved.evaluation.Groups.Single().Guidance.Single(g=>g.RecordId=="2").ExistingCatalogCode=="EXISTING","existing approved classification untouched");

        // 2: actual mixed branch/outcome conflicts, full case closure and independent groups.
        var array=N("2"); array.Measurement.Parameters["cad_array_handle"]="A1";
        var mixed=Run(N("1"),array,N("3",key:"OTHER-GROUP"));
        Check(mixed.prepared.Snapshot.Records.Single(g=>g.RecordId=="2").State==C.State.PendingLineage,"actual ED1 array pending used");
        Check(mixed.evaluation.Groups.Single(g=>g.RuleKey=="GROUP").State==P.Disposition.PendingContext,"mixed ordinary and pending group visible nonactionable");
        Check(mixed.evaluation.Groups.Single(g=>g.RuleKey=="OTHER-GROUP").State==P.Disposition.RuleReview,"independent ordinary guidance survives mixed group");
        Check(mixed.evaluation.Groups.SelectMany(g=>g.MemberIds).Order().SequenceEqual(new[]{"1","2","3"}),"complete member conservation");
        var differentParts=Run(N("1"),N("2",layer:"EDGE"));
        Check(differentParts.evaluation.Groups.Single().State==P.Disposition.MixedReview&&NoEligible(differentParts.evaluation),"different actual parts same key cannot become shared raw proposal");
        foreach(var layer in new[]{"OLD_KERB","UNKNOWN"})
        {
            var outcome=Run(N("1"),N("2",layer:layer));
            Check(outcome.evaluation.Groups.Single().State==P.Disposition.MixedReview&&NoEligible(outcome.evaluation),"mixed actual rule plus "+layer+" visible review");
        }
        var onlyExcluded=Run(N("1",layer:"OLD_KERB"));
        Check(onlyExcluded.evaluation.Groups.Single().State==P.Disposition.RecordReview&&NoEligible(onlyExcluded.evaluation),"all excluded means review not exclusion action");
        var units=Run(N("1",project:"OTHER"),N("2",project:"OTHER",kind:"area",unit:"m2"));
        Check(units.evaluation.Groups.Single().State==P.Disposition.MixedReview&&NoEligible(units.evaluation),"generic mixed unit and kind cannot inherit eligibility");
        var unknownUnits=Run(N("1",project:"OTHER",unit:"unknown-a"),N("2",project:"OTHER",unit:"unknown-b"));
        Check(NoEligible(unknownUnits.evaluation),"two unknown units are not equal by canonical question mark");
        var caseArray=N("2",key:"group"); caseArray.Measurement.Parameters["cad_array_handle"]="A1";
        var cases=Run(N("1"),caseArray);
        Check(cases.evaluation.Groups.Count==2&&cases.evaluation.Groups.All(g=>g.FullReviewMemberIds.SequenceEqual(new[]{"1","2"})),"exact keys retained full case-insensitive closure recorded");
        Check(cases.evaluation.Groups.All(g=>g.State==P.Disposition.PendingContext)&&NoEligible(cases.evaluation),"case variant cannot inherit subset eligibility");

        // 3: stamp + full record-content freshness; no reliance on Snapshot.Digest.
        foreach(var stamp in new[]{generic.request.Current with{CatalogSha256=H('3')},generic.request.Current with{ProfileSha256=H('3')},
            generic.request.Current with{RulesSha256=H('3')},generic.request.Current with{SourceSetSha256=H('3')},
            generic.request.Current with{ResolutionReceiptSha256=H('3')}})
        {
            var e=P.Evaluate(generic.prepared,stamp,generic.request.ActiveRecords);
            Check(!e.ContextMatches&&e.ContextReason=="stale_context"&&NoEligible(e),"stale stamp field refuses generic eligibility "+passed.Count);
        }
        foreach(var field in new[]{"raw","unit","layer","parameters","classification","status","civil_identity","finding"})
        {
            var f=Run(N("1",project:"OTHER"),N("2",project:"OTHER"));
            var changed=JsonNode.Parse(JsonSerializer.Serialize(f.request.ActiveRecords))!;
            var r=changed[0]!;
            switch(field) {
                case "raw": r["measurement"]!["raw_value"]=4; break;
                case "unit": r["measurement"]!["unit"]="m2"; break;
                case "layer": r["source"]!["layer"]="CHANGED"; break;
                case "parameters": r["measurement"]!["parameters"]!["new"]="changed"; break;
                case "classification": r["classification"]!["candidate_catalog_code"]="NEW"; break;
                case "status": r["status"]="Blocked"; break;
                case "civil_identity": r["source"]!["civil_identity"]="changed"; break;
                default: r["findings"]!.AsArray().Add(JsonNode.Parse("""{"finding_id":"fixed","code":"TEST","domain":"estimate","severity":"Warning","title":"changed","created_at_utc":"2026-10-01T00:00:00Z"}""")); break;
            }
            var e=P.Evaluate(f.prepared,f.request.Current,changed.Deserialize<NeutralQuantityRecord[]>()!);
            Check(!e.ContextMatches&&e.ContextReason=="records_changed"&&NoEligible(e),"full member content changed: "+field);
        }
        var subset=P.Evaluate(generic.prepared,generic.request.Current,generic.request.ActiveRecords.Take(1).ToArray());
        Check(!subset.ContextMatches&&subset.CapturedMemberIds.Count==2&&subset.CurrentMemberIds.Count==1&&NoEligible(subset),"removed member explicit captured/current coverage");
        var removedAll=P.Evaluate(generic.prepared,generic.request.Current,Array.Empty<NeutralQuantityRecord>());
        Check(!removedAll.ContextMatches&&removedAll.Groups.Count==0&&removedAll.CapturedMemberIds.Count==2&&!string.IsNullOrWhiteSpace(removedAll.ContextMessage),"all removed members retain visible global context refusal");
        var added=P.Evaluate(generic.prepared,generic.request.Current,generic.request.ActiveRecords.Append(N("3",project:"OTHER")).ToArray());
        Check(!added.ContextMatches&&added.CurrentMemberIds.Count==3&&NoEligible(added),"new record visible not silently covered");
        var duplicate=P.Evaluate(generic.prepared,generic.request.Current,new[]{generic.request.ActiveRecords[0],generic.request.ActiveRecords[0]});
        Check(duplicate.ContextReason=="invalid_members"&&NoEligible(duplicate),"duplicate member refused");
        var traced=generic.prepared.Snapshot with{Digest="trace-not-authenticity"};
        var traceOnly=P.Capture(traced,generic.request.ActiveRecords);
        Check(P.Evaluate(traceOnly,generic.request.Current,generic.request.ActiveRecords).Groups.Single().GenericProposalEligible,"Snapshot.Digest not treated as authenticity");

        // 4: refusal/capture boundary and ranker-facing eligibility only (no service hooks claimed).
        Throws(()=>P.Capture(positive.prepared.Snapshot,positive.request.ActiveRecords.Take(1).ToArray()),"capture rejects subset guidance");
        Throws(()=>P.Capture(positive.prepared.Snapshot with{Records=positive.prepared.Snapshot.Records.Concat(positive.prepared.Snapshot.Records.Take(1)).ToArray()},positive.request.ActiveRecords),"capture rejects duplicate guidance");
        var wrongRaw=positive.prepared.Snapshot with{Records=positive.prepared.Snapshot.Records.Select(g=>g with{OriginalRaw=99}).ToArray()};
        Throws(()=>P.Capture(wrongRaw,positive.request.ActiveRecords),"capture rejects changed raw handoff");
        Check(typeof(P.Group).GetProperty("Quantity")==null&&typeof(P.Group).GetProperty("Price")==null&&typeof(P.Group).GetProperty("ProposedCode")==null,"scope API has no quantity price or proposed-code output");
        Check(positive.evaluation.Groups.Concat(generic.evaluation.Groups).Where(g=>g.GenericProposalEligible).All(g=>g.State==P.Disposition.GenericUnchanged),"single ranker eligibility admits generic only");

        // 5: deterministic detached outputs and preservation, no input writes or recapture.
        var before=JsonSerializer.Serialize(positive.request.ActiveRecords);
        var second=P.Evaluate(positive.prepared,positive.request.Current,positive.request.ActiveRecords.Reverse().ToArray());
        Check(JsonSerializer.Serialize(second)==JsonSerializer.Serialize(positive.evaluation),"input permutation stable output");
        Check(JsonSerializer.Serialize(positive.request.ActiveRecords)==before,"evaluate does not mutate records");
        var output=JsonSerializer.Serialize(positive.evaluation);
        positive.request.ActiveRecords[0].Measurement.Parameters["later"]="mutation";
        Check(JsonSerializer.Serialize(positive.evaluation)==output,"output detached from later input mutation");
        bool immutable=false;try{((IList<P.Group>)positive.evaluation.Groups).Clear();}catch(NotSupportedException){immutable=true;}
        Check(immutable,"groups collection read-only");
        var nan=JsonNode.Parse(JsonSerializer.Serialize(generic.request.ActiveRecords))!;
        nan[0]!["measurement"]!["raw_value"]="NaN";
        Check(NoEligible(P.Evaluate(generic.prepared,generic.request.Current,nan.Deserialize<NeutralQuantityRecord[]>()!)),"nonfinite member cannot become eligible");
        var empty=Run(Array.Empty<NeutralQuantityRecord>());
        Check(empty.evaluation.ContextMatches&&empty.evaluation.Groups.Count==0,"empty scope conserved");
        var many=Run(Enumerable.Range(0,100).Select(i=>N(i.ToString(),key:"GROUP-"+i,project:"OTHER")).ToArray());
        Check(many.evaluation.Groups.Count==100&&many.evaluation.Groups.All(g=>g.GenericProposalEligible&&g.MemberIds.Count==1),"100 independent groups retain useful eligibility");
        passed.Should().HaveCountGreaterThan(40).And.OnlyHaveUniqueItems();
    }
}
