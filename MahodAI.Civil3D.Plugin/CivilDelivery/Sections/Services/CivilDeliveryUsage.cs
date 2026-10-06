using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.Utilities;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Mahod Impact usage records for Mahod Civil Delivery (<see cref="MahodUsage"/>; contract
    /// docs/telemetry.md, "Desktop products", in devMahod/mahod-imapct). Added 2026-10-06 for
    /// 1.4.2; the same records MahodAI already sends for the Civil Delivery it carries
    /// (MahodAI plugin, <c>Ported/CivilDelivery/.../CivilDeliveryUsage.cs</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalog prices Civil Delivery by the section produced AND verified (feature
    /// <c>section_delivery</c>), so the unit is recorded where every verification is persisted,
    /// <see cref="SectionsWorkflowService.PersistVerifyEvidence"/>: one unit per record whose
    /// read-back status is Verified, keyed by the drawing's fingerprint and the record's logical
    /// key (hashed by <see cref="MahodUsage"/>, never sent), so verifying the same section again
    /// in a month is priced once.
    /// </para>
    /// <para>
    /// Actions (counted, never priced): the section steps plan, preview, apply and verify, and
    /// the early estimate's quantity scan (<c>civildelivery_estimate</c>). Each workflow method
    /// opens an <see cref="Operation"/> so the record carries how long the step ran; a step that
    /// throws or returns without its record is sent as <c>failed</c>. There is no
    /// <c>cancelled</c>: a step either returns or throws.
    /// </para>
    /// <para>
    /// Door: <c>palette</c> unless the caller set <see cref="MahodUsage.AmbientDoor"/> — the
    /// standalone product's typed <c>MCD_*</c> commands set <c>standalone</c>
    /// (CivilDeliveryCommandFacade). Deliberately NOT decided by assembly name: this source is
    /// also compiled into this repository's MahodAI fork, whose assembly is named
    /// MahodAI.Civil3D.Plugin like the real MahodAI plugin. A step under the chat door records
    /// no action (the MahodAI executor counts chat calls itself); its units still count.
    /// </para>
    /// </remarks>
    internal static class CivilDeliveryUsage
    {
        internal const string Tool = "civildelivery";

        internal const string Feature = "section_delivery";

        internal const string PlanAction = "civildelivery_plan";
        internal const string PreviewAction = "civildelivery_preview";
        internal const string ApplyAction = "civildelivery_apply";
        internal const string VerifyAction = "civildelivery_verify";
        internal const string EstimateAction = "civildelivery_estimate";

        // The standalone product's typed commands (MahodAI records MHD_* under the same names).
        internal const string OpenAction = "civildelivery_open";
        internal const string SetupAction = "civildelivery_setup";
        internal const string SectionsAction = "civildelivery_sections";

        [ThreadStatic] private static Operation? _current;

        /// <summary>Starts timing one workflow step on this thread; dispose it when the step returns or throws.</summary>
        internal static Operation Begin(string action)
        {
            var op = new Operation(action, _current);
            _current = op;
            return op;
        }

        /// <summary>One section or estimate step finished (<paramref name="ok"/> = completed, else failed).</summary>
        public static void Step(string action, bool ok)
        {
            try
            {
                long elapsed = 0;
                var op = _current;
                if (op != null && !op.Recorded && string.Equals(op.Action, action, StringComparison.Ordinal))
                {
                    op.Recorded = true;
                    elapsed = op.ElapsedMs;
                }
                Record(action, ok, elapsed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CivilDeliveryUsage.Step: " + ex.Message);
            }
        }

        /// <summary>A verification was persisted: the verify step, then one unit per section it found verified.</summary>
        public static void Verified(Document doc, SectionVerifyResult result)
        {
            try
            {
                Step(VerifyAction, result.Status != DeliveryStatus.Failed);
                string drawing;
                try
                {
                    drawing = doc.Database.FingerprintGuid;
                }
                catch (Exception)
                {
                    return;
                }
                string door = MahodUsage.DoorOr(MahodUsage.Palette);
                foreach (var key in UnitKeys(drawing, result))
                    MahodUsage.Unit(Tool, Feature, key, door);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CivilDeliveryUsage.Verified: " + ex.Message);
            }
        }

        /// <summary>
        /// The unit keys of one verification (pure, tested): drawing fingerprint + "|section:" + the
        /// record's logical key (its record id when it has none), for each record read back Verified.
        /// </summary>
        internal static IReadOnlyList<string> UnitKeys(string drawing, SectionVerifyResult result) =>
            result.Records
                .Where(r => r.Status == DeliveryStatus.Verified)
                .Select(r => drawing + "|section:" + (string.IsNullOrEmpty(r.LogicalKey) ? r.RecordId : r.LogicalKey))
                .ToList();

        private static void Record(string action, bool ok, long elapsedMs)
        {
            if (MahodUsage.AmbientDoor == MahodUsage.Chat) return;
            MahodUsage.Action(Tool, action, ok ? MahodUsage.Completed : MahodUsage.Failed, elapsedMs,
                MahodUsage.DoorOr(MahodUsage.Palette));
        }

        /// <summary>
        /// One timed workflow step. <see cref="Step"/> records it; disposing it unrecorded (the step
        /// threw, or returned on a path with no record) sends it as failed. Nested steps (VERIFY's
        /// own re-PLAN) restore the outer one on dispose.
        /// </summary>
        internal sealed class Operation : IDisposable
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly Operation? _outer;

            internal Operation(string action, Operation? outer)
            {
                Action = action;
                _outer = outer;
            }

            internal string Action { get; }

            internal bool Recorded { get; set; }

            internal long ElapsedMs => _clock.ElapsedMilliseconds;

            public void Dispose()
            {
                try
                {
                    if (!Recorded)
                    {
                        Recorded = true;
                        Record(Action, ok: false, ElapsedMs);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("CivilDeliveryUsage.Operation: " + ex.Message);
                }
                finally
                {
                    if (ReferenceEquals(_current, this)) _current = _outer;
                }
            }
        }
    }
}
