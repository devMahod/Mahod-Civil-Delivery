using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Source contract of the recognition-evidence collector. These tests prove only the code shape (read-only
    /// Autodesk access, call order in the scan); no Autodesk member was executed. Native behaviour needs Civil.
    /// </summary>
    public sealed class CivilEvidenceCollectorSourceContractTests
    {
        private static string Source(string name)
        {
            var sourceRoot = typeof(CivilEvidenceCollectorSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            return File.ReadAllText(Path.Combine(sourceRoot, "CivilDelivery", "Estimate", name));
        }

        [Fact]
        public void CollectorOnlyReadsThroughTheScanTransaction()
        {
            var collector = Source("CivilEvidenceCollector.cs");
            collector.Should().NotContain("OpenMode.ForWrite")
                .And.NotContain("ReadDwgFile")
                .And.NotContain("StartTransaction")
                .And.NotContain("AttachXref")
                .And.NotContain("UpgradeOpen")
                .And.NotContain("GetXrefDatabase")
                .And.NotContain("DeliveryFinding", "the collector never emits a finding per record")
                .And.NotContain(".ToArray(", "matrices are serialized through the explicit indexer")
                .And.NotContain("\"not-collected\"", "PropertySets are read (CivilPropertySetReader), never a fixed placeholder");
            collector.Should().Contain("OpenMode.ForRead")
                .And.Contain("values[row * 4 + column] = matrix[row, column];")
                .And.Contain("EvidenceValue.Unavailable(ex.GetType().Name)")
                .And.Contain("LegendEvidence.ForRecord(_legend, _legendFailure, record.Measurement.Parameters)")
                .And.Contain("Put(EvidenceKeys.PsetComponent, () => _propertySets.Read(ent, tr, insideReference: frame.Ancestors != null));")
                .And.Contain("EvidenceValue.Unavailable(\"database-mismatch\")");
            Regex.Matches(collector, @"tr\.GetObject\([^)]*\)").Cast<Match>()
                .Should().OnlyContain(match => match.Value.Contains("OpenMode.ForRead"));
        }

        [Fact]
        public void PropertySetReaderOpensObjectsForReadOnlyAndNeverThrowsOutOfRead()
        {
            var reader = Source("CivilPropertySetReader.cs");
            reader.Should().NotContain("OpenMode.ForWrite")
                .And.NotContain("UpgradeOpen")
                .And.NotContain("StartTransaction")
                .And.NotContain("SetAt(")
                .And.NotContain("SetData(")
                .And.NotContain("Synchronize(")
                .And.NotContain("AddPropertySet(")
                .And.NotContain("RemovePropertySet(")
                .And.NotContain("throw ", "a failed read becomes a status, never an exception of the reader's own");
            var opens = Regex.Matches(reader, @"tr\.GetObject\([^)]*\)").Cast<Match>().ToList();
            opens.Should().HaveCount(2, "the set and its definition are the only objects opened");
            opens.Should().OnlyContain(match => match.Value.Contains("OpenMode.ForRead"));
            reader.Should().Contain("PropertyDataServices.GetPropertySets(ent)")
                .And.Contain("propertySet.ObjectAttachedTo != owner")
                .And.Contain("catch (Exception) { unread++; }")
                .And.Contain("catch (Exception) { unreadSets++; }")
                .And.Contain("catch (Exception ex) { ModuleFailure = \"pset-module-\" + ex.GetType().Name; }")
                .And.Contain("if (ModuleFailure != null) return EvidenceValue.Unavailable(ModuleFailure);")
                .And.Contain("if (insideReference) return EvidenceValue.Unavailable(\"pset-reference-context-unread\");")
                .And.Contain("property.Automatic ||")
                .And.Contain("sets.Add(new PropertySetEvidence.Set(propertySet.PropertySetDefinitionName, properties, unread, derived));")
                .And.Contain("return PropertySetEvidence.Build(sets, unreadSets);");
            // No field or property holds an AEC type (an AecDataType enum field would need the assembly just to lay the class
            // out), and the AEC types are reached only through methods the JIT never inlines into Read.
            Regex.Matches(reader, @"(private|internal|public|protected)\s+(static\s+|readonly\s+)*(AecPropertyData\.|AecDataType\b|Autodesk\.Aec\.)[\w.]*\??\s+\w+\s*[;={]")
                .Count.Should().Be(0, "no field holds an AEC type: a host without AecPropDataMgd still loads the reader");
            reader.Should().MatchRegex(@"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static string\? ModuleState\(\)")
                .And.MatchRegex(@"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s+private static EvidenceValue ReadAttached\(Entity ent, Transaction tr, ref int examinedInScan\)");
            // Bounded deterministically over the scan too; a read stopped part-way is reported on every record in Complete.
            reader.Should().Contain("if (++examinedInScan > MaxScanPropertiesRead) return EvidenceValue.Unavailable(ScanCapReason);")
                .And.Contain("if (ScanFailure != null) return EvidenceValue.Unavailable(ScanFailure);");

            // Every exception of the reader ends in the collector's Put as unavailable:<ExceptionType>.
            var collector = Source("CivilEvidenceCollector.cs");
            collector.Should().Contain("Put(EvidenceKeys.PsetComponent, () => _propertySets.Read(ent, tr, insideReference: frame.Ancestors != null));")
                .And.Contain("catch (Exception ex) { value = EvidenceValue.Unavailable(ex.GetType().Name); }")
                .And.Contain("private readonly CivilPropertySetReader _propertySets = new();")
                .And.Contain("if (_propertySets.ScanFailure is { } psetStopped)")
                .And.Contain("EvidenceJson.Write(record.Measurement.Parameters, EvidenceKeys.PsetComponent, EvidenceValue.Unavailable(psetStopped));")
                .And.NotContain("Autodesk.Aec", "every AEC type stays inside CivilPropertySetReader");
        }

        [Fact]
        public void ScanCapturesTextFirstReadsEvidenceAfterTheTransformAndBindsEmittedRecords()
        {
            var source = Source("CivilQuantityExtractionService.cs");
            var created = source.IndexOf("new CivilEvidenceCollector(", StringComparison.Ordinal);
            created.Should().BeGreaterThan(0);
            source.IndexOf("new CivilEvidenceCollector(", created + 1, StringComparison.Ordinal)
                .Should().Be(-1, "one collector and one text index per scan");

            var consider = source.IndexOf("void Consider(", StringComparison.Ordinal);
            var capture = source.IndexOf("evidence.CaptureText(ent, transform, handlePath, source.XrefChain)", consider, StringComparison.Ordinal);
            var sourceLayer = source.IndexOf("var sourceLayer = ent.Layer;", consider, StringComparison.Ordinal);
            var natural = source.IndexOf("var measurements = NaturalMeasurements", consider, StringComparison.Ordinal);
            var cad = source.IndexOf("QuantityCadMetadataPolicy.AppendEvidence(measurement, cadEvidence)", natural, StringComparison.Ordinal);
            var transform = source.IndexOf("if (transformMeasurement)", cad, StringComparison.Ordinal);
            var read = source.IndexOf("var entityEvidence = evidence.Read(ent, tr, frame, transform, handlePath, source.XrefChain);", transform, StringComparison.Ordinal);
            var append = source.IndexOf("CivilEvidenceCollector.Append(measurement, entityEvidence);", read, StringComparison.Ordinal);
            var classify = source.IndexOf("var ruleKey = BuildDiscoveryRuleKey(ent.Layer, measurement)", append, StringComparison.Ordinal);
            var added = source.IndexOf("result.Records.Add(record);", classify, StringComparison.Ordinal);
            var bind = source.IndexOf("evidence.Bind(record, entityEvidence);", added, StringComparison.Ordinal);
            capture.Should().BeGreaterThan(consider);
            sourceLayer.Should().BeGreaterThan(capture, "text on presentation layers is indexed before the exclusion");
            natural.Should().BeGreaterThan(sourceLayer);
            transform.Should().BeGreaterThan(cad);
            read.Should().BeGreaterThan(transform, "evidence describes the final, transformed measurements");
            append.Should().BeGreaterThan(read);
            classify.Should().BeGreaterThan(append);
            bind.Should().BeGreaterThan(added);
            source[capture..sourceLayer].Should().NotContain("ScannedEntities").And.NotContain("return;")
                .And.NotContain("bucket", "text capture changes no scan count");

            var traversal = source.IndexOf("foreach (ObjectId id in ms)", StringComparison.Ordinal);
            var complete = source.IndexOf("result.EvidenceCoverage = evidence.Complete();", traversal, StringComparison.Ordinal);
            var validate = source.IndexOf("originalReferences.ValidateSourcesUnchanged()", StringComparison.Ordinal);
            complete.Should().BeGreaterThan(traversal);
            validate.Should().BeGreaterThan(complete);
        }

        [Fact]
        public void FramesFollowOrdinaryInsertsAndXrefBoundaries()
        {
            var source = Source("CivilQuantityExtractionService.cs");
            source.Should().Contain("Consider(reference, parentSource, outerTransform, currentHandle, frame,")
                .And.Contain("var insideFrame = frame.EnterInsert(reference, evidence, tr);")
                .And.Contain("depth + 1, composedTransform, definitionStack, insideFrame,")
                .And.Contain("depth + 1, composedTransform, definitionStack, xrefFrame,")
                .And.Contain("new HashSet<ObjectId>(), hostFrame, isModelSpaceReference: true,")
                .And.Contain("Consider(entity, hostSource, Matrix3d.Identity, entity.Handle.ToString(), hostFrame);")
                .And.Contain("else if (member is DBText or MText)");
            var freshness = source.IndexOf("if (freshnessFailure != null)", StringComparison.Ordinal);
            var transformCheck = source.IndexOf("if (!transformCheck.IsSafe)", freshness, StringComparison.Ordinal);
            var xrefFrame = source.IndexOf("var xrefFrame = frame.EnterXref(reference, definition, loadedDatabase, evidence, tr);",
                transformCheck, StringComparison.Ordinal);
            xrefFrame.Should().BeGreaterThan(transformCheck, "only a proven-fresh, similarity XREF gets an evidence frame");
        }

        [Fact]
        public void EveryOrdinaryInsertIndexesItsVisibleAttributesOnceWhetherOrNotItIsCounted()
        {
            var source = Source("CivilQuantityExtractionService.cs");
            var ordinary = source.IndexOf("if (!isExternal)", StringComparison.Ordinal);
            var capture = source.IndexOf("evidence.CaptureAttributes(reference, tr, outerTransform, currentHandle,", ordinary, StringComparison.Ordinal);
            var counted = source.IndexOf("if (countOrdinaryReference)", ordinary, StringComparison.Ordinal);
            capture.Should().BeGreaterThan(ordinary).And.BeLessThan(counted,
                "a nested implementation INSERT (countOrdinaryReference=false) still shows its attributes");
            var collector = Source("CivilEvidenceCollector.cs");
            Regex.Matches(collector, @"\bCaptureAttribute\(attribute,").Count.Should().Be(1,
                "attributes are indexed in CaptureAttributes only; reading ev_block_attributes never indexes them again");
            collector.Should().Contain("private static EvidenceValue ReadAttributes(BlockReference reference, Transaction tr)");
        }

        [Fact]
        public void AFailedTextReadIsReportedToTheIndexNotOnlyCounted()
        {
            var collector = Source("CivilEvidenceCollector.cs");
            collector.Should().Contain("private void Unread(Point3d? anchor, Matrix3d sourceToHost, string handlePath)")
                .And.Contain("_texts.ReportUnread(host.X * _units.LinearToMetres, host.Y * _units.LinearToMetres, handlePath);")
                .And.Contain("_texts.ReportUnread(null, null, handlePath);");
            Regex.Matches(collector, @"_captureFailures\+\+").Count.Should().Be(1, "every capture failure goes through Unread");
            // A failed attribute keeps its own leaf path (INSERT path + attribute handle), as a readable one does.
            collector.Should().Contain("leafPath = CivilQuantityExtractionService.AppendReferenceHandlePath(handlePath, id.Handle.ToString());")
                .And.Contain("Unread(anchor, sourceToHost, leafPath);");
        }

        [Fact]
        public void LegendsAreReadOnceReadOnlyInTheScanTransactionBeforeComplete()
        {
            var service = Source("CivilQuantityExtractionService.cs");
            var legends = service.IndexOf("evidence.ReadLegends(tr, db);", StringComparison.Ordinal);
            var complete = service.IndexOf("result.EvidenceCoverage = evidence.Complete();", StringComparison.Ordinal);
            legends.Should().BeGreaterThan(0).And.BeLessThan(complete);
            var sheetQa = Path.Combine(typeof(CivilEvidenceCollectorSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!,
                "Services", "SheetQA");
            foreach (var file in new[] { "LegendBlockReader.cs", "SheetStyleResolver.cs" })
                File.ReadAllText(Path.Combine(sheetQa, file)).Should().NotContain("ForWrite").And.NotContain("UpgradeOpen")
                    .And.NotContain("StartTransaction", "the legend reader only reads through the scan transaction");
            // Evidence mode (int-b review): the collector opts in; SheetQA's reads keep today's behaviour.
            var reader = File.ReadAllText(Path.Combine(sheetQa, "LegendBlockReader.cs"));
            reader.Should().Contain("public bool ReadForEvidence { get; init; }")
                .And.Contain("if (ReadForEvidence) ReadOtherNamedLegends(candidates, chosen, named != null, result);")
                .And.Contain("if (UnreadXref(chosen.Definition)) result.NoteReadFailure();")
                .And.Contain("(definition.IsUnloaded || !definition.IsResolved)")
                .And.Contain("if (ReadForEvidence && (!ent.Visible ||")
                .And.Contain("internal static bool MatchesLegendName(string? blockName) => MatchesLegendName(blockName, forEvidence: false);")
                .And.Contain("!(forEvidence && hint is \"key-plan\" or \"keyplan\")");
            var collector = Source("CivilEvidenceCollector.cs");
            collector.Should().Contain("new LegendBlockReader(tr, new SheetStyleResolver(tr, db)) { ReadForEvidence = true }")
                .And.Contain("EvidenceJson.Write(fields, EvidenceKeys.LegendRow, EvidenceValue.Unavailable(\"not-completed\"));")
                .And.Contain("EvidenceJson.Write(record.Measurement.Parameters, EvidenceKeys.LegendRow, LegendFor(record));");
        }

        [Fact]
        public void CoverageIsPublishedOnceBesideTheOtherScanDiagnostics()
        {
            var workflow = Source("EstimateWorkflowService.cs");
            workflow.Should().Contain("result.EvidenceCoverage = extraction.EvidenceCoverage;")
                .And.Contain("\"evidence_coverage.json\", evidenceCoverage, pendingRoot);")
                .And.Contain("internal EvidenceCoverage? EvidenceCoverage { get; set; }");
        }

        [Fact]
        public void RecognitionEvidenceCannotChangeARuleKey()
        {
            var measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'",
                Parameters = { ["block_name"] = "SYNTHETIC-BLOCK" },
            };
            var before = CivilQuantityExtractionService.BuildDiscoveryRuleKey("SYNTHETIC|asdasd23423", measurement);
            measurement.Parameters["ev_block_attributes"] = "[{\"tag\":\"CODE\",\"value\":\"U51.06.1900\"}]";
            measurement.Parameters["ev_block_attributes_status"] = "read";
            measurement.Parameters["ev_nearby_text"] = "[{\"text\":\"אבן שפה\"}]";
            measurement.Parameters["ev_nearby_text_status"] = "read";
            CivilQuantityExtractionService.BuildDiscoveryRuleKey("SYNTHETIC|asdasd23423", measurement)
                .Should().Be(before);
        }
    }
}
