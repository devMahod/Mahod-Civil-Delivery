using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed record SpatialBoundarySelection(string DrawingId, string Handle);

internal sealed class EstimateSpatialBoundary
{
    internal ObjectId BoundaryId { get; }
    private readonly SpatialQuantityClip _clip;
    private readonly string _handle;
    private EstimateSpatialBoundary(ObjectId id, string handle, SpatialQuantityClip clip)
        => (BoundaryId, _handle, _clip) = (id, handle, clip);

    internal static EstimateSpatialBoundary Resolve(Database db, Transaction tr, SpatialBoundarySelection selection)
    {
        if (db.FingerprintGuid != selection.DrawingId)
            throw new InvalidOperationException("התחום שייך לשרטוט אחר. בחר תחום בשרטוט הפעיל או לחץ נקה תחום");
        var id = db.GetObjectId(false, new Handle(long.Parse(selection.Handle, NumberStyles.HexNumber)), 0);
        if (tr.GetObject(id, OpenMode.ForRead) is not Polyline polyline || !polyline.Closed ||
            polyline.NumberOfVertices < 3 || Math.Abs(Math.Abs(polyline.Normal.Z)-1) > 1e-9 ||
            Enumerable.Range(0, polyline.NumberOfVertices).Any(i => polyline.GetBulgeAt(i) != 0))
            throw new InvalidOperationException("יש לבחור פוליליין סגור במישור XY עם מקטעים ישרים בלבד");
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (polyline.OwnerId != bt[BlockTableRecord.ModelSpace])
            throw new InvalidOperationException("הגבול צריך להיות ב-Model Space של השרטוט הפעיל");
        var points = Enumerable.Range(0, polyline.NumberOfVertices).Select(polyline.GetPoint3dAt);
        return new(id, selection.Handle, new SpatialQuantityClip(points.Select(p => (p.X,p.Y))));
    }

    internal QuantityMeasurement? Clip(Entity entity, Transaction tr, Matrix3d transform, QuantityMeasurement measurement,
        double linearToMetres)
    {
        if (entity.ObjectId == BoundaryId) return null;
        double? value = null;
        if (entity is BlockReference block && measurement.Kind == "count")
        {
            var p = block.Position.TransformBy(transform);
            if (!_clip.Contains(p.X,p.Y)) return null;
            value = measurement.RawValue;
        }
        else if (entity is Line line && measurement.Kind == "length")
            value = Length(line.StartPoint.TransformBy(transform), line.EndPoint.TransformBy(transform)) * linearToMetres;
        else if (entity is Polyline poly && Enumerable.Range(0,poly.NumberOfVertices).All(i => poly.GetBulgeAt(i)==0))
        {
            var points = Enumerable.Range(0,poly.NumberOfVertices).Select(i=>poly.GetPoint3dAt(i).TransformBy(transform)).ToArray();
            if (measurement.Kind == "area" && poly.Closed && points.All(p=>Math.Abs(p.Z-points[0].Z)<1e-8))
                value = _clip.Area(points.Select(p=>(p.X,p.Y))) * linearToMetres * linearToMetres;
            else if (measurement.Kind == "length")
            {
                double length=0;
                for(var i=0;i<points.Length-(poly.Closed?0:1);i++) length+=Length(points[i],points[(i+1)%points.Length]);
                value=length*linearToMetres;
            }
        }
        else if (entity is Polyline2d oldPoly && oldPoly.PolyType == Poly2dType.SimplePoly)
        {
            var vertices=oldPoly.Cast<ObjectId>().Select(id=>(Vertex2d)tr.GetObject(id,OpenMode.ForRead)).ToArray();
            if(vertices.All(v=>v.Bulge==0))
                value=ClipPath(vertices.Select(v=>new Point3d(v.Position.X,v.Position.Y,oldPoly.Elevation)
                    .TransformBy(Matrix3d.PlaneToWorld(oldPoly.Normal)).TransformBy(transform)).ToArray(),oldPoly.Closed,measurement.Kind,linearToMetres);
        }
        else if (entity is Polyline3d poly3d && poly3d.PolyType == Poly3dType.SimplePoly)
            value=ClipPath(poly3d.Cast<ObjectId>().Select(id=>((PolylineVertex3d)tr.GetObject(id,OpenMode.ForRead)).Position.TransformBy(transform)).ToArray(),
                poly3d.Closed,measurement.Kind,linearToMetres);
        if (value == null)
        {
            // Bounds are used only to prove complete inclusion/exclusion. Crossing unsupported geometry fails closed.
            var extents=entity.GeometricExtents;
            var corners=new List<Point3d>();
            foreach(var x in new[]{extents.MinPoint.X,extents.MaxPoint.X})
            foreach(var y in new[]{extents.MinPoint.Y,extents.MaxPoint.Y})
            foreach(var z in new[]{extents.MinPoint.Z,extents.MaxPoint.Z})
                corners.Add(new Point3d(x,y,z).TransformBy(transform));
            var envelope=new NetTopologySuite.Geometries.Envelope(corners.Min(p=>p.X),corners.Max(p=>p.X),corners.Min(p=>p.Y),corners.Max(p=>p.Y));
            var bounds=_clip.Boundary.Factory.ToGeometry(envelope);
            if (!_clip.Boundary.Intersects(bounds)) return null;
            if (!_clip.Boundary.Covers(bounds))
                throw new InvalidOperationException("גאומטריה חוצה שאינה נתמכת לחיתוך מדויק: "+entity.GetType().Name);
            value=measurement.RawValue;
        }
        if (!double.IsFinite(value.Value) || value < 0) throw new InvalidOperationException("תוצאת חיתוך אינה תקינה");
        if (value <= 1e-12) return null;
        var parameters=new Dictionary<string,string>(measurement.Parameters)
        {
            ["quantity_boundary_handle"]=_handle,
            ["quantity_before_boundary"]=measurement.RawValue.ToString("R",CultureInfo.InvariantCulture),
            ["quantity_boundary_method"]="host-XY-exact-intersection",
        };
        return new QuantityMeasurement {Kind=measurement.Kind,Method=measurement.Method,RawValue=value.Value,
            Unit=measurement.Unit,GeometryEvidence=measurement.GeometryEvidence,Parameters=parameters};
    }
    private double Length(Point3d a, Point3d b)=>_clip.SegmentLength((a.X,a.Y,a.Z),(b.X,b.Y,b.Z));

    private double? ClipPath(Point3d[] points,bool closed,string kind,double scale)
    {
        if(points.Length<2) return null;
        if(kind=="area" && closed && points.All(p=>Math.Abs(p.Z-points[0].Z)<1e-8))
            return _clip.Area(points.Select(p=>(p.X,p.Y)))*scale*scale;
        if(kind!="length") return null;
        double length=0;
        for(var i=0;i<points.Length-(closed?0:1);i++) length+=Length(points[i],points[(i+1)%points.Length]);
        return length*scale;
    }

    internal bool IsOutside(Entity entity, Matrix3d transform)
    {
        if (entity.ObjectId == BoundaryId) return true;
        try
        {
            if (entity is BlockReference block)
            {
                var p=block.Position.TransformBy(transform);
                return !_clip.Contains(p.X,p.Y);
            }
            var e=entity.GeometricExtents;
            var corners=new List<Point3d>();
            foreach(var x in new[]{e.MinPoint.X,e.MaxPoint.X})
            foreach(var y in new[]{e.MinPoint.Y,e.MaxPoint.Y})
            foreach(var z in new[]{e.MinPoint.Z,e.MaxPoint.Z})
                corners.Add(new Point3d(x,y,z).TransformBy(transform));
            var envelope=new NetTopologySuite.Geometries.Envelope(corners.Min(p=>p.X),corners.Max(p=>p.X),corners.Min(p=>p.Y),corners.Max(p=>p.Y));
            return !_clip.Boundary.Intersects(_clip.Boundary.Factory.ToGeometry(envelope));
        }
        catch { return false; } // Missing extents never prove exclusion; retain the normal measurement failure.
    }
}
