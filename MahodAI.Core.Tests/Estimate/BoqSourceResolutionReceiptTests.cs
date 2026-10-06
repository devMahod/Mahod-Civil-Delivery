using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using R = MahodAI.CivilDelivery.Estimate.BoqRulesV2.BoqSourceResolutionReceipt;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>Codex L05 focused checks (l05-producer-receipt-candidate), ported 1:1 to xunit; frozen-DLL pins dropped.</summary>
public sealed class BoqSourceResolutionReceiptTests
{
    [Fact]
    public void AllFocusedChecksPass()
    {

        var checks=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new Exception(name);checks.Add(name);}
        void Refuse(Action action,string name){try{action();}catch(ArgumentException){checks.Add(name);return;}throw new Exception(name);}
        string Sha(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        string H(char c)=>new(c,64);
        byte[] Bytes(string text)=>Encoding.UTF8.GetBytes(text);
        R.Artifact Read(R.ArtifactKind kind,string run,string json="[]")
        {
            var bytes=Bytes(json); return R.CaptureJson<JsonElement>(kind,"C:/runs/"+run+"/"+kind+".json",bytes,kind==R.ArtifactKind.Manifest?null:Sha(bytes)).Evidence;
        }
        R.Artifact[] Artifacts(string run)=>Enum.GetValues<R.ArtifactKind>().Select(k=>Read(k,run)).ToArray();
        var stamp=new DateTime(2026,10,1,10,0,0,DateTimeKind.Utc);
        R.Choice Chosen(string role,string run,bool active=false)
        {
            var artifacts=Artifacts(run);
            var manifest=artifacts.Single(a=>a.Kind==R.ArtifactKind.Manifest);
            var candidate=new R.Candidate(run,manifest.Path!,manifest.ActualSha256!,"C:/local/"+role+".dwg",stamp,0);
            return new(role,active?R.SelectionState.Active:R.SelectionState.Selected,run,candidate.DrawingPath,H('a'),R.DrawingState.Unchanged,H('a'),null,stamp,
                active?Array.Empty<R.Candidate>():new[]{candidate},artifacts);
        }
        R.Choice Missing(string role)=>new(role,R.SelectionState.Missing,null,null,null,R.DrawingState.NotChecked,null,"no_matching_run",null,
            Array.Empty<R.Candidate>(),Enum.GetValues<R.ArtifactKind>().Select(k=>R.NotRead(k,null,R.ReadState.NotRead,"no_selected_run")).ToArray());
        R.Request Request()=>new("existing-latest-in-folder/v1",H('b'),"a1","C:/local",R.InventoryState.Complete,Array.Empty<R.Artifact>(),
            new[]{new R.Role("A","-A-",0),new R.Role("B","-B-",1),new R.Role("C","-C-",2)},new[]{Chosen("A","a1",true),Chosen("B","b1"),Missing("C")});

        // Same frozen bytes are hashed and deserialized; paths are identifiers, never opened.
        var bytes=Bytes("[{\"value\":95,\"unit\":\"מטר\"}]");
        var rawSha=Sha(bytes);
        var parsed=R.CaptureJson<JsonElement>(R.ArtifactKind.Records,"does-not-exist.json",bytes,rawSha.ToLowerInvariant());
        Check(parsed.Success&&parsed.Evidence.ActualSha256==rawSha&&parsed.Value[0].GetProperty("value").GetInt32()==95,"same supplied bytes hash and parse, no path I/O");
        Array.Fill(bytes,(byte)'x');
        Check(parsed.Evidence.ActualSha256==rawSha&&parsed.Value[0].GetProperty("unit").GetString()=="מטר","caller byte mutation does not alter captured parse/identity");
        var whitespace=Bytes("[ {\"value\":95,\"unit\":\"מטר\"} ]");
        var different=R.CaptureJson<JsonElement>(R.ArtifactKind.Records,"p",whitespace,Sha(whitespace));
        Check(different.Success&&different.Evidence.ActualSha256!=rawSha&&different.Value[0].GetProperty("value").GetInt32()==95,"byte whitespace identity differs despite equal semantic value");
        var bom=new byte[]{0xEF,0xBB,0xBF}.Concat(whitespace).ToArray();
        var bomResult=R.CaptureJson<JsonElement>(R.ArtifactKind.Records,"p",bom,Sha(bom));
        Check(bomResult.Success&&bomResult.Value[0].GetProperty("value").GetInt32()==95&&bomResult.Evidence.ActualSha256==Sha(bom)&&bomResult.Evidence.ActualSha256!=Sha(whitespace),"UTF8 BOM parsed compatibly while full-byte hash retains BOM");
        var bad=R.CaptureJson<JsonElement>(R.ArtifactKind.Records,"p",whitespace,H('0'));
        Check(!bad.Success&&bad.Evidence.State==R.ReadState.HashMismatch,"hash mismatch has no parsed success");
        Check(R.CaptureJson<JsonElement>(R.ArtifactKind.Records,"p",whitespace,null).Evidence.State==R.ReadState.Unlisted,"unlisted not accepted as parsed evidence");
        var malformed=Bytes("{broken");
        Check(R.CaptureJson<JsonElement>(R.ArtifactKind.Findings,"p",malformed,Sha(malformed)).Evidence.State==R.ReadState.ParseFailed,"malformed bytes preserve parse-failed identity");
        var nul=Bytes("null");
        Check(R.CaptureJson<object>(R.ArtifactKind.Records,"p",nul,Sha(nul)).Evidence.State==R.ReadState.ParseFailed,"JSON null is not an invented empty records list");

        var request=Request(); var receipt=R.Build(request);
        Check(receipt.Observed.Choices.Count==3&&receipt.Observed.Choices.Single(c=>c.RoleId=="C").State==R.SelectionState.Missing,"positive active selected and missing roles conserved");
        Check(!receipt.IsApproval&&!receipt.ProvesFreshness,"receipt never claims approval or disk freshness");
        var permuted=request with{Roles=request.Roles.Reverse().ToArray(),Choices=request.Choices.Reverse().Select(c=>c with{Artifacts=c.Artifacts.Reverse().ToArray()}).ToArray()};
        Check(R.Build(permuted).Sha256==receipt.Sha256,"role/choice/artifact input order deterministic");
        var detached=JsonSerializer.Serialize(receipt);
        ((R.Choice[])request.Choices)[1]=Missing("B");
        Check(JsonSerializer.Serialize(receipt)==detached,"receipt detached from caller collections");
        bool readOnly=false;try{((IList<R.Choice>)receipt.Observed.Choices).Clear();}catch(NotSupportedException){readOnly=true;}
        Check(readOnly,"receipt choices immutable collection");

        request=Request();
        R.Request WithArtifact(R.Request req,R.ArtifactKind kind,R.Artifact replacement)=>req with{Choices=req.Choices.Select(c=>c.RoleId=="B"?
            c with{Artifacts=c.Artifacts.Select(a=>a.Kind==kind?replacement:a).ToArray()}:c).ToArray()};
        var absent=WithArtifact(request,R.ArtifactKind.Findings,R.NotRead(R.ArtifactKind.Findings,"findings.json",R.ReadState.Missing,"file_absent"));
        var inaccessible=WithArtifact(request,R.ArtifactKind.Findings,R.NotRead(R.ArtifactKind.Findings,"findings.json",R.ReadState.Unavailable,"access_denied"));
        Check(R.Build(absent).Sha256!=R.Build(inaccessible).Sha256,"optional missing and unavailable produce distinct receipts");
        Check(R.Build(inaccessible).Observed.Choices.Single(c=>c.RoleId=="B").Artifacts.Single(a=>a.Kind==R.ArtifactKind.Findings).State==R.ReadState.Unavailable,"unavailable stays visible, not an empty finding list");
        var newRole=request with{Choices=request.Choices.Select(c=>c.RoleId=="C"?Chosen("C","c1"):c).ToArray()};
        Check(R.Build(newRole).Sha256!=R.Build(request).Sha256,"previously missing role appearing changes resolution receipt");
        var changedDiagnostics=WithArtifact(request,R.ArtifactKind.HatchDiagnostics,Read(R.ArtifactKind.HatchDiagnostics,"b1","{\"changed\":true}"));
        Check(R.Build(changedDiagnostics).Sha256!=R.Build(request).Sha256,"optional diagnostics byte change invalidates receipt equality");
        var b=request.Choices.Single(c=>c.RoleId=="B");
        var failedB=b with{State=R.SelectionState.Rejected,DrawingState=R.DrawingState.Changed,CurrentDrawingSha256=H('c'),ReasonCode="drawing_changed"};
        var rejected=request with{Choices=request.Choices.Select(c=>c.RoleId=="B"?failedB:c).ToArray()};
        Check(R.Build(rejected).Observed.Choices.Single(c=>c.RoleId=="B").RunId=="b1"&&R.Build(rejected).Sha256!=R.Build(request).Sha256,"rejected latest run identity retained, no fallback selected");
        var partial=request with{InventoryState=R.InventoryState.Partial,InventoryFailures=new[]{R.NotRead(R.ArtifactKind.Manifest,"unknown/run_manifest.json",R.ReadState.Unavailable,"read_failed")}};
        Refuse(()=>R.Build(partial),"incomplete discovery cannot label role missing");
        partial=partial with{Choices=partial.Choices.Select(c=>c.RoleId=="C"?c with{State=R.SelectionState.Unavailable,ReasonCode="inventory_incomplete"}:c).ToArray()};
        Check(R.Build(partial).Observed.InventoryState==R.InventoryState.Partial,"partial discovery represented honestly with unavailable role");
        var failurePair=new[]{R.NotRead(R.ArtifactKind.Manifest,"same-path",R.ReadState.Unavailable,"read_failed"),R.NotRead(R.ArtifactKind.ScanMetadata,"same-path",R.ReadState.Unavailable,"read_failed")};
        Check(R.Build(partial with{InventoryFailures=failurePair}).Sha256==R.Build(partial with{InventoryFailures=failurePair.Reverse().ToArray()}).Sha256,"same-path different-kind inventory failures deterministic");

        // Selection order is the owner's actual order, not a new latest/tie-break implementation.
        var older=b.Candidates.Single() with{RunId="b0",ManifestPath="C:/runs/b0/manifest.json",ManifestSha256=H('d'),ObservedRank=1};
        var more=request with{Choices=request.Choices.Select(c=>c.RoleId=="B"?c with{Candidates=c.Candidates.Append(older).ToArray()}:c).ToArray()};
        var reorder=more with{Choices=more.Choices.Select(c=>c with{Candidates=c.Candidates.Reverse().ToArray()}).ToArray()};
        Check(R.Build(more).Sha256==R.Build(reorder).Sha256,"candidate array permutation preserves captured rank");
        var rankChange=more with{Choices=more.Choices.Select(c=>c.RoleId=="B"?c with{Candidates=c.Candidates.Select(x=>x with{ObservedRank=1-x.ObservedRank}).ToArray()}:c).ToArray()};
        Check(R.Build(more).Sha256!=R.Build(rankChange).Sha256,"changed observed tie order remains detectable, not silently canonicalized away");
        Refuse(()=>R.Build(request with{Choices=request.Choices.Take(2).ToArray()}),"omitted role refused");
        Refuse(()=>R.Build(request with{Choices=new[]{request.Choices[0],request.Choices[0],request.Choices[2]}}),"duplicate role refused");
        Refuse(()=>R.Build(request with{Choices=request.Choices.Select(c=>c.RoleId=="B"?c with{Artifacts=c.Artifacts.Where(a=>a.Kind!=R.ArtifactKind.HatchDiagnostics).ToArray()}:c).ToArray()}),"omitted optional evidence state refused");
        Refuse(()=>R.Build(request with{Choices=request.Choices.Select(c=>c.RoleId=="B"?c with{RunId="unobserved"}:c).ToArray()}),"selected run absent from inventory refused");
        Refuse(()=>R.Build(WithArtifact(request,R.ArtifactKind.Manifest,Read(R.ArtifactKind.Manifest,"other-run"))),"same manifest bytes at another path cannot inherit selected identity");
        Refuse(()=>R.Build(request with{Choices=request.Choices.Select(c=>c.RoleId=="B"?c with{CurrentDrawingSha256=H('e')}:c).ToArray()}),"selected changed drawing contradiction refused");
        Refuse(()=>R.Build(WithArtifact(request,R.ArtifactKind.ScanMetadata,R.NotRead(R.ArtifactKind.ScanMetadata,"scan.json",R.ReadState.NotRead,"not_read"))),"known scan time cannot lack metadata evidence");
        var unknownTime=WithArtifact(request,R.ArtifactKind.ScanMetadata,R.NotRead(R.ArtifactKind.ScanMetadata,"scan.json",R.ReadState.Missing,"legacy_missing"));
        unknownTime=unknownTime with{Choices=unknownTime.Choices.Select(c=>c.RoleId=="B"?c with{ScannedAtUtc=null}:c).ToArray()};
        Check(R.Build(unknownTime).Observed.Choices.Single(c=>c.RoleId=="B").ScannedAtUtc==null,"unknown original scan time remains unknown");
        Refuse(()=>R.Build(WithArtifact(request,R.ArtifactKind.Findings,Read(R.ArtifactKind.Findings,"b1") with{State=R.ReadState.Missing,ReasonCode="fake_missing"})),"missing cannot carry claimed read bytes");
        Refuse(()=>R.NotRead(R.ArtifactKind.Records,"p",R.ReadState.Parsed,"fake"),"unread factory cannot fabricate parsed state");
        Assert.NotEmpty(checks);
    }
}
