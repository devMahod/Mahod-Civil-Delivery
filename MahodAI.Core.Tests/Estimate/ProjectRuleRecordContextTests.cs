using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using C = MahodAI.CivilDelivery.Estimate.ProjectRuleRecordContext;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>Codex L05 focused checks (context-binding-candidate), ported 1:1 to xunit; frozen-DLL pins dropped.</summary>
public sealed class ProjectRuleRecordContextTests
{
    [Fact]
    public void AllFocusedChecksPass()
    {

        var tests = new List<string>();
        var json = new JsonSerializerOptions { NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals };
        void Check(bool value, string name) { if(!value) throw new Exception(name); tests.Add(name); }
        C.Request Fixture(Action<List<NeutralQuantityRecord>>? change=null, string project="CTX-P", bool hatch=false, bool crosswalk=false)
        {
            var records = new List<NeutralQuantityRecord> { N("n1","1","KERB",3), N("n2","2","KERB",7) };
            change?.Invoke(records);
            if(project!="CTX-P") records=records.Select(n=>new NeutralQuantityRecord {
                RecordId=n.RecordId,ProjectProfileId=project,RunId=n.RunId,Source=n.Source,Measurement=n.Measurement,
                Classification=n.Classification,Status=n.Status,Findings=n.Findings,Provenance=n.Provenance }).ToList();
            var rulesText="""
            {"schema":"mahod-boq-rules/2","project":"CTX-P","version":"1",
             "parameters":[{"id":"road_class","label":"road","value":1},{"id":"cross_w","label":"width","value":3},{"id":"fill","label":"share","value":0.5}],"existing_layers_prefix":"OLD_",
             "chapters":[{"id":"10.01","title":"test"}],
             "source_roles":[{"id":"A","file_pattern":"-A-"}],
             "boq":[{"id":"K","chapter":"10.01","item":"10.01.0010","unit":"m",
                "confirm":"נדרש אישור הנדסי","review":"בדיקה בלבד",
                "parts":[{"label":"אבן שפה","src":["A"],"layers":["KERB"],"kind":"length","object_width_m":0.3}]}]}
            """;
            if(hatch) rulesText=rulesText.Replace("\"kind\":\"length\",\"object_width_m\":0.3","\"kind\":\"hatch\"").Replace("\"unit\":\"m\"","\"unit\":\"m2\"");
            if(crosswalk) rulesText=rulesText.Replace("\"kind\":\"length\",\"object_width_m\":0.3","\"kind\":\"crosswalk_geo\"").Replace("\"unit\":\"m\"","\"unit\":\"m2\"")
                .Replace("\"boq\":", "\"crosswalk_geometry\":{\"src\":[\"A\"],\"white_layers\":[\"KERB\"],\"default_standard_width\":3,\"standard_width_param\":\"cross_w\",\"fill_param\":\"fill\"},\"boq\":");
            var rules=BoqRuleset.Parse(rulesText);
            var scan=new BoqNeutralRecordAdapter.SourceScan("A","run","C:/local/test-A-model.dwg",H('a'),records,Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") };
            var id=new C.SourceIdentity("A","run",scan.DrawingPath,scan.DrawingHash,H('b'),C.RecordDigest(records),H('c'),C.AuxiliaryDigest(scan));
            var sources=new[]{new C.Source(id,scan)};
            var input=BoqNeutralRecordAdapter.Build(rules,new[]{scan});
            var result=BoqRulesEngine.Run(rules,input);
            var stamp=new C.Stamp(project,"run",scan.DrawingPath,scan.DrawingHash,H('e'),H('f'),H('1'),
                rules.Sha256,"snapshot",H('2'),C.SourceSetDigest(sources.Select(s=>s.Identity)),H('4'),C.OutcomeDigest(result));
            return new C.Request(stamp,stamp,sources,records,result);
        }
        NeutralQuantityRecord N(string id,string handle,string layer,double raw,string method="line-length",string unit="m",
            string kind="length",string? xref=null,string etype="LINE", Dictionary<string,string>? parameters=null) => new()
        {
            RecordId=id,ProjectProfileId="CTX-P",RunId="run",
            Source=new QuantitySource { Drawing="test-A-model.dwg",DrawingPath="C:/local/test-A-model.dwg",DrawingHash=H('a'),
                Handle=handle,Layer=layer,EntityType=etype,Xref=xref },
            Measurement=new QuantityMeasurement { Kind=kind,Unit=unit,Method=method,RawValue=raw,Parameters=parameters??new() },
            Classification=new QuantityClassification { RuleKey="layer:"+layer+"|"+kind },
        };
        string H(char c)=>new(c,64);
        bool Pending(C.Snapshot s)=>s.Records.All(x=>x.State==C.State.PendingLineage);
        C.Request WithSource(C.Request f, IReadOnlyList<NeutralQuantityRecord> records)
        {
            var scan=f.Sources[0].Scan with { Records=records };
            var id=f.Sources[0].Identity with {RecordObjectSha256=C.RecordDigest(records)};
            var sources=new[]{new C.Source(id,scan)};
            var stamp=f.Captured with { SourceSetSha256=C.SourceSetDigest(sources.Select(x=>x.Identity)) };
            return f with { Sources=sources,ActiveRecords=records,Captured=stamp,Current=stamp };
        }
        C.Request ReSealForAdversarialProducer(C.Request f)
        {
            var stamp=f.Captured with {OutcomeSha256=C.OutcomeDigest(f.Result)};
            return f with {Captured=stamp,Current=stamp};
        }
        var p=Fixture();
        var before=JsonSerializer.Serialize(p.ActiveRecords,json);
        var positive=C.Bind(p);
        Check(positive.Records.Count==2 && positive.Records.All(x=>x.State==C.State.RuleReview),"positive ordinary 2/2");
        Check(positive.Records.All(x=>x.LineId=="K"&&x.PartIndex==0&&x.RuleCatalogCode=="10.01.0010"&&x.RuleUnit=="m"&&x.ObjectWidth==.3),"actual engine part guidance");
        Check(positive.Records.All(x=>x.Confirmation=="נדרש אישור הנדסי"&&x.Review=="בדיקה בלבד"&&!x.IsApproval&&!x.IsPrice),"confirmation retained not approval");
        Check(JsonSerializer.Serialize(p.ActiveRecords,json)==before,"raw inputs unchanged");
        Check(positive.Records.Select(x=>x.OriginalRaw).SequenceEqual(new[]{3d,7d}),"raw not physical allocation");
        Check(typeof(C.Guidance).GetProperty("Quantity")==null && typeof(C.Guidance).GetProperty("Price")==null,"no payable quantity/price API");

        var nongeneric=Fixture(project:"OTHER-PROJECT");
        var unrelated=C.Bind(nongeneric);
        Check(unrelated.Records.All(x=>x.State==C.State.GenericUnchanged&&x.RuleCatalogCode==null),"non6422 different-project generic unchanged");
        Check(unrelated.Records.Select(x=>(x.RecordId,x.OriginalRaw,x.OriginalUnit)).SequenceEqual(positive.Records.Select(x=>(x.RecordId,x.OriginalRaw,x.OriginalUnit))),"non-applicable members unchanged");

        var approved=Fixture(r=>r[0].Classification.CandidateCatalogCode="EXISTING-APPROVED");
        var ap=C.Bind(approved);
        Check(ap.Records[0].ExistingCatalogCode=="EXISTING-APPROVED"&&approved.ActiveRecords[0].Classification.CandidateCatalogCode=="EXISTING-APPROVED","existing mapping not overwritten");
        Check(ap.Records[0].RuleCatalogCode=="10.01.0010"&&!ap.Records[0].IsApproval,"rule guidance separate from existing mapping");

        var unclassified=Fixture(r=>r[1]=N("n2","2","UNKNOWN",7));
        Check(C.Bind(unclassified).Records[1].State==C.State.UnclassifiedReview,"actual engine unclassified preserved");
        var excluded=Fixture(r=>r[1]=N("n2","2","OLD_KERB",7));
        Check(excluded.Result.Buckets[1]==BoqBucket.Excluded&&C.Bind(excluded).Records[1].State==C.State.ExcludedReview,"actual engine exclusion retained");

        Check(Pending(C.Bind(p with {Current=p.Current with {CatalogSha256=H('3')}})),"changed catalog stale");
        Check(Pending(C.Bind(p with {Current=p.Current with {ProfileSha256=H('3')}})),"changed profile stale");
        Check(Pending(C.Bind(p with {Current=p.Current with {SourceSetSha256=H('3')}})),"changed source set stale");
        Check(Pending(C.Bind(p with {Current=p.Current with {ResolutionReceiptSha256=H('3')}})),"missing/new source selection stale");
        Check(Pending(C.Bind(p with {Captured=p.Captured with {RulesSha256=H('3')},Current=p.Current with {RulesSha256=H('3')}})),"wrong rules SHA");
        Check(Pending(C.Bind(p with {Captured=p.Captured with {ScanSha256="bad"}})),"invalid receipt hash");
        Check(Pending(C.Bind(p with {ActiveRecords=p.ActiveRecords.Take(1).ToArray()})),"subset active members refused");
        Check(Pending(C.Bind(p with {ActiveRecords=p.ActiveRecords.Concat(new[]{N("n3","3","KERB",9)}).ToArray()})),"new active record not covered");
        Check(Pending(C.Bind(p with {ActiveRecords=new[]{p.ActiveRecords[0],p.ActiveRecords[0]}})),"duplicate active IDs refused");
        var dup=Fixture(r=>r[1]=N("n2","1","KERB",7));
        Check(Pending(C.Bind(dup)),"duplicate handle-kind refused");
        var orphan=Fixture(); orphan.Result.Input.Records.Add(new BoqRecord("A","KERB","LINE","length",2,"phantom",false,"",null));
        Check(Pending(C.Bind(orphan)),"extra engine member refused");
        var changed=Fixture(); changed.Result.Input.Records[0]=changed.Result.Input.Records[0] with {Qty=4};
        Check(Pending(C.Bind(ReSealForAdversarialProducer(changed))),"changed engine raw refused even with matching outcome receipt");
        foreach(var field in new[]{"Layer","Kind","Etype","Block","Closed","Bbox"})
        {
            var f=Fixture(); var b=f.Result.Input.Records[0];
            f.Result.Input.Records[0]=field switch {
                "Layer"=>b with{Layer="OTHER"},"Kind"=>b with{Kind="area"},"Etype"=>b with{Etype="HATCH"},
                "Block"=>b with{Block="OTHER"},"Closed"=>b with{Closed=true},_=>b with{Bbox=new[]{1d,2,3,4}}
            };
            Check(Pending(C.Bind(ReSealForAdversarialProducer(f))),"mismatch "+field+" despite recaptured outcome");
        }
        var unit=Fixture(); var units=unit.ActiveRecords.ToArray(); units[0]=N("n1","1","KERB",3,unit:"m2");
        unit=WithSource(unit,units); Check(Pending(C.Bind(unit)),"source unit mismatch");
        var source=Fixture(); var si=source.Sources[0].Identity with {DrawingSha256=H('9')};
        Check(Pending(C.Bind(source with {Sources=new[]{new C.Source(si,source.Sources[0].Scan)}})),"source identity mismatch");
        var tamper=Fixture(); tamper.ActiveRecords[0].Measurement.Parameters["new"]="changed";
        Check(Pending(C.Bind(tamper)),"producer object hash mismatch after mutation");
        var xref=Fixture(r=>r[1]=N("n2","2","KERB",7,xref:"child.dwg"));
        var xs=C.Bind(xref);
        Check(xs.Records[0].State==C.State.RuleReview&&xs.Records[1].State==C.State.PendingLineage,"xref pending ordinary sibling retained");
        var recovered=Fixture(r=>r[1]=N("n2","2","KERB",7,method:"hatch-line-arc-boundary-area",kind:"area",unit:"m2",etype:"HATCH"));
        Check(C.Bind(recovered).Records[1].State==C.State.PendingLineage,"recovery pending not outside");
        var array=Fixture(r=>r[1]=N("n2","2","KERB",7,parameters:new(){{"cad_array_handle","A1"}}));
        Check(C.Bind(array).Records[1].State==C.State.PendingLineage,"array skipped branch pending");
        var hatch=Fixture(r=>r[1]=N("n2","2","KERB",7,method:"hatch-area",kind:"area",unit:"m2",etype:"HATCH"),hatch:true);
        Check(hatch.Result.Input.HatchLayers.Count==1 && hatch.Result.Input.Records.Count==1,"actual hatch aggregate branch exercised");
        Check(C.Bind(hatch).Records[1].State==C.State.PendingLineage,"aggregated hatch not outside context");
        var cw=Fixture(r=> {
         for(var i=0;i<r.Count;i++) {r[i].Measurement.Parameters["cad_segments"]=$"0,{i};10,{i}";
         r[i].Measurement.Parameters["cad_segments_status"]="complete";r[i].Measurement.Parameters["cad_entity_database_insunits"]="6";
         r[i].Measurement.Parameters["cad_entity_linetype_scale"]="0.5";}
        },crosswalk:true);
        var cws=C.Bind(cw);
        Check(cw.Result.Crossings.Count>0 && cws.Records.All(x=>x.State==C.State.RuleReview),"actual crossing branch useful guidance");
        Check(cws.Records.All(x=>x.CrossingIndices?.Count>0 && x.CrossingRole!=null),"crossing roles and member indices retained without allocation");
        var absent=Fixture(); absent.Result.Input.Records.RemoveAt(1);
        Check(Pending(C.Bind(absent)),"missing result index shape");
        var part=Fixture(); part.Result.Items[0]=("K",999);
        Check(C.Bind(ReSealForAdversarialProducer(part)).Records[0].State==C.State.PendingLineage,"invalid part index despite recaptured outcome");
        var inconsistent=Fixture(); inconsistent.Result.Buckets[0]=BoqBucket.Excluded;
        Check(C.Bind(ReSealForAdversarialProducer(inconsistent)).Records[0].State==C.State.PendingLineage,"excluded still claimed inconsistent despite recaptured outcome");
        var reordered=p with {ActiveRecords=p.ActiveRecords.Reverse().ToArray()};
        Check(C.Bind(reordered).Records.OrderBy(x=>x.RecordId).Select(x=>x.State).SequenceEqual(positive.Records.Select(x=>x.State)),"member permutation state invariant");
        var persisted=JsonSerializer.Serialize(positive,json);
        p.ActiveRecords[0].Classification.CandidateCatalogCode="later"; p.Result.Buckets[0]=BoqBucket.Excluded;
        Check(JsonSerializer.Serialize(positive,json)==persisted,"snapshot detached from later source/result mutation");
        Check(positive.Records is System.Collections.IList il&&il.IsReadOnly,"output collection read-only");
        var empty=Fixture(r=>r.Clear()); Check(C.Bind(empty).Records.Count==0,"empty conserved");
        var changedAux=Fixture();
        var changedAuxSource=changedAux.Sources[0] with {Scan=changedAux.Sources[0].Scan with {ScannedAtUtc=DateTime.UnixEpoch}};
        Check(Pending(C.Bind(changedAux with {Sources=new[]{changedAuxSource}})),"auxiliary evidence changed after producer receipt");
        var negativeZero=Fixture();negativeZero.Result.Input.Records[0]=negativeZero.Result.Input.Records[0] with {Qty=-0d};
        Check(Pending(C.Bind(negativeZero)),"bit-exact raw mismatch");
        var nan=Fixture();nan.Result.Input.Records[0]=nan.Result.Input.Records[0] with {Qty=double.NaN};
        Check(Pending(C.Bind(ReSealForAdversarialProducer(nan))),"nonfinite engine raw refused despite recaptured outcome");
        var badBBox=Fixture();badBBox.Result.Input.Records[0]=badBBox.Result.Input.Records[0] with {Bbox=new[]{double.NaN,0,1,1}};
        Check(Pending(C.Bind(ReSealForAdversarialProducer(badBBox))),"nonfinite bbox refused despite recaptured outcome");
        var duplicateRoles=Fixture();var ds=duplicateRoles.Sources.Concat(duplicateRoles.Sources).ToArray();
        var dsStamp=duplicateRoles.Captured with {SourceSetSha256=C.SourceSetDigest(ds.Select(s=>s.Identity))};
        Check(Pending(C.Bind(duplicateRoles with {Sources=ds,Captured=dsStamp,Current=dsStamp})),"two scans for one source role refused");
        var oldDigest=positive.Digest;
        Check(oldDigest==positive.Digest && positive.Records.All(x=>x.ExistingCatalogCode==null),"frozen guidance does not inherit later approval");

        // Actual embedded 6422 rules + production adapter/engine, with synthetic source receipts.
        // This is a mixed branch usability test, not a saved/native scan reproduction.
        var realRules=BoqRuleset.LoadEmbedded6422();
        NeutralQuantityRecord RealRecord(string id,string handle,string layer,string kind,string method,double qty,string type,string role,Dictionary<string,string>? parameters=null)=>new(){
            RecordId=id,ProjectProfileId="6422",RunId="run-"+role,
            Source=new QuantitySource {Drawing="6422-"+role+"-model.dwg",DrawingPath="C:/local/6422-"+role+"-model.dwg",DrawingHash=H('a'),Handle=handle,Layer=layer,EntityType=type},
            Measurement=new QuantityMeasurement{Kind=kind,Method=method,RawValue=qty,Unit=kind=="length"?"מטר":kind=="area"?"מ\"ר":"יחידה",Parameters=parameters??new()},
            Classification=new QuantityClassification{RuleKey="layer:"+layer+"|"+kind},
        };
        var gmRecords=new[]{
            RealRecord("gm-normal","101","HW-CURB","length","line-length",4,"LINE","GM"),
            RealRecord("gm-array","102","BIKE","count","block-count",1,"INSERT","GM",new(){{"cad_array_handle","ARRAY1"},{"block_name","BIKE"}}),
            RealRecord("gm-recovered","103","OTHER","area","hatch-line-arc-boundary-area",2,"HATCH","GM"),
        };
        var haRecords=new[]{RealRecord("ha-aggregate","201","HA-AREA","area","hatch-area",7,"HATCH","HA")};
        C.Source RealSource(string role,IReadOnlyList<NeutralQuantityRecord> rs) {
            var scan=new BoqNeutralRecordAdapter.SourceScan(role,"run-"+role,"C:/local/6422-"+role+"-model.dwg",H('a'),rs,Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") };
            return new C.Source(new C.SourceIdentity(role,scan.RunId,scan.DrawingPath,scan.DrawingHash,H('b'),C.RecordDigest(rs),H('c'),C.AuxiliaryDigest(scan)),scan);
        }
        var realSources=new[]{RealSource("GM",gmRecords),RealSource("HA",haRecords)};
        var realInput=BoqNeutralRecordAdapter.Build(realRules,realSources.Select(s=>s.Scan).ToArray());
        var realResult=BoqRulesEngine.Run(realRules,realInput);
        var realStamp=new C.Stamp("6422","run-GM",realSources[0].Identity.DrawingPath,H('a'),H('b'),H('c'),H('d'),realRules.Sha256,"test-catalog",H('e'),C.SourceSetDigest(realSources.Select(s=>s.Identity)),H('f'),C.OutcomeDigest(realResult));
        var mixedRequest=new C.Request(realStamp,realStamp,realSources,gmRecords,realResult);
        var mixed=C.Bind(mixedRequest);
        Check(realInput.Records.Any(r=>r.Handle=="102"),"real 6422 adapter emits legitimate array member");
        Check(realInput.HatchLayers.Count>0 && realInput.StrictHatchRecoveries.ContainsKey("103"),"real 6422 aggregate and recovery branches exercised");
        Check(mixed.Records.Single(r=>r.RecordId=="gm-normal") is {State:C.State.RuleReview,LineId:"C1",RuleCatalogCode:"51.06.0010"},"mixed real engine ordinary C1 remains useful");
        Check(mixed.Records.Single(r=>r.RecordId=="gm-array").State==C.State.PendingLineage && mixed.Records.Single(r=>r.RecordId=="gm-recovered").State==C.State.PendingLineage,"mixed array/recovery pending without poisoning ordinary");
        var arrayIndex=realInput.Records.FindIndex(r=>r.Handle=="102");
        realInput.Records[arrayIndex]=realInput.Records[arrayIndex] with {Qty=99};
        Check(Pending(C.Bind(mixedRequest)),"tampered unsupported emitted member still poisons context");
        var forgedStamp=realStamp with {OutcomeSha256=C.OutcomeDigest(realResult)};
        Check(Pending(C.Bind(mixedRequest with {Captured=forgedStamp,Current=forgedStamp})),"exact source matching still rejects changed array even with recaptured outcome");
        var swapped=Fixture();
        (swapped.Result.Input.Records[0],swapped.Result.Input.Records[1])=(swapped.Result.Input.Records[1],swapped.Result.Input.Records[0]);
        Check(Pending(C.Bind(swapped)),"input reorder after Run cannot inherit old index outcomes");
        var crossProject=Fixture();var wrongStamp=crossProject.Captured with {ProjectId="OTHER"};
        Check(Pending(C.Bind(crossProject with {Captured=wrongStamp,Current=wrongStamp})),"different-project exit cannot mask mismatched active record project");

        Assert.NotEmpty(tests);
    }
}
