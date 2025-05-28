// File: IfcToGeoJson.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xbim.Common;
using Xbim.Common.Configuration;
using Xbim.Common.Geometry;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.ModelGeometry.Scene;

namespace IfcToGeoJson
{
    public class GeometryResult
    {
        public List<List<double[]>> Polygons { get; set; } = new();
        public string? Error { get; set; }
    }

    class Program
    {
        private static ILoggerFactory? LoggerFactory;
        private static ILogger? Logger;
        private const double CoordinateScale = 0.00001;

        static int Main(string[] args)
        {
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());
            Logger = LoggerFactory.CreateLogger<Program>();

            XbimServices.Current.ConfigureServices(s =>
                s.AddXbimToolkit(c => c.AddLoggerFactory(LoggerFactory)));

            if (args.Length < 2)
            {
                Logger?.LogError("Usage: IfcToGeoJson <ifcPath> <outputPath> [elementGuid]");
                return 1;
            }

            try
            {
                bool single = args.Length > 2;
                string ifcPath = args[0];
                string outputPath = args[1];
                string geojson = single
                    ? ProcessSingleElement(ifcPath, args[2])
                    : ProcessAllElements(ifcPath);

                File.WriteAllText(outputPath, geojson);
                Logger?.LogInformation("Successfully created GeoJSON at: {Path}", outputPath);
                return 0;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Conversion failed");
                File.WriteAllText(
                    args.Length >= 2 ? args[1] : "error_output.geojson",
                    JsonConvert.SerializeObject(new
                    {
                        type = "FeatureCollection",
                        features = new object[0],
                        error = ex.Message,
                        stackTrace = ex.StackTrace
                    }, Formatting.Indented)
                );
                return 1;
            }
        }

        public static string ProcessSingleElement(string ifcPath, string elementGuid)
        {
            using var store = IfcStore.Open(ifcPath);
            var element = store.Instances.FirstOrDefault<IIfcProduct>(e => e.GlobalId == elementGuid)
                          ?? throw new Exception($"Element {elementGuid} not found");

            var context = new Xbim3DModelContext(store);
            Logger?.LogInformation("Creating geometry context...");
            context.CreateContext();

            var shapes = context.ShapeInstances().Where(s => s.IfcProductLabel == element.EntityLabel).ToList();
            if (!shapes.Any())
                Logger?.LogWarning("No shape instances found for element {Guid}", elementGuid);

            var result = GetElementGeometry(store, context, element);
            var feature = CreateGeoJsonFeature(elementGuid, element.GetType().Name, result.Polygons);

            var geojson = new JObject(
                new JProperty("type", "FeatureCollection"),
                new JProperty("features", new JArray(feature))
            );

            return geojson.ToString(Formatting.Indented);
        }

        public static string ProcessAllElements(string ifcPath)
        {
            using var store = IfcStore.Open(ifcPath);
            var context = new Xbim3DModelContext(store);
            Logger?.LogInformation("Creating geometry context...");
            context.CreateContext();

            var allShapes = context.ShapeInstances().ToList();
            Logger?.LogInformation("Geometry context contains {Count} shape instances", allShapes.Count);

            int productCount = store.Instances
                .OfType<IIfcProduct>()
                .Count(p => p.Representation != null);
            Logger?.LogInformation("Found {Count} products with geometry", productCount);

            var features = new List<JObject>();
            foreach (var product in store.Instances
                                         .OfType<IIfcProduct>()
                                         .Where(p => p.Representation != null))
            {
                var result = GetElementGeometry(store, context, product);
                if (result.Polygons.Any())
                {
                    features.Add(CreateGeoJsonFeature(
                        product.GlobalId,
                        product.GetType().Name,
                        result.Polygons
                    ));
                }
            }

            return new JObject(
                new JProperty("type", "FeatureCollection"),
                new JProperty("features", new JArray(features))
            ).ToString(Formatting.Indented);
        }

        private static GeometryResult GetElementGeometry(
            IfcStore store,
            Xbim3DModelContext context,
            IIfcProduct element)
        {
            var result = new GeometryResult();
            var instances = context.ShapeInstances()
                                   .Where(si => si.IfcProductLabel == element.EntityLabel)
                                   .ToList();

            if (!instances.Any())
            {
                Logger?.LogWarning("→ No shape instances for product {Id}", element.GlobalId);
                return result;
            }

            Logger?.LogInformation("  → Product {Id} has {N} shape instances",
                                   element.GlobalId, instances.Count);

            foreach (var inst in instances)
                ProcessShapeInstance(context, inst, result);

            return result;
        }

        private static void ProcessShapeInstance(
            Xbim3DModelContext context,
            IXbimShapeInstanceData instance,
            GeometryResult result)
        {
            var geom = context.ShapeGeometry(instance.ShapeGeometryLabel);
            if (geom == null)
            {
                Logger?.LogWarning("    → No geometry object for label {Label}", instance.ShapeGeometryLabel);
                return;
            }

            var vertices = geom.Vertices?.Cast<XbimPoint3D>().ToList();
            var faces = geom.Faces?.ToList();

            Logger?.LogInformation("→ Geometry Vertices = {V}, Faces = {F}", vertices?.Count ?? 0, faces?.Count ?? 0);

            if (vertices == null || faces == null || vertices.Count == 0)
                return;

            foreach (var face in faces)
            {
                Logger?.LogInformation("    Face type: {Type}", face.GetType().FullName);

                if (face is Xbim.Common.Geometry.WexBimMeshFace meshFace)
                {
                    var indices = meshFace.Indices?.ToArray();
                    if (indices == null || indices.Length % 3 != 0)
                    {
                        Logger?.LogWarning("      → Skipping face due to invalid triangle count");
                        continue;
                    }

                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int v0 = indices[i], v1 = indices[i + 1], v2 = indices[i + 2];
                        if (v0 < 0 || v1 < 0 || v2 < 0 ||
                            v0 >= vertices.Count || v1 >= vertices.Count || v2 >= vertices.Count)
                        {
                            Logger?.LogWarning("      → Skipping triangle with out-of-bound indices");
                            continue;
                        }

                        var points = new[] { vertices[v0], vertices[v1], vertices[v2] };
                        if (points.Any(p => double.IsNaN(p.X) || double.IsInfinity(p.X) ||
                                            double.IsNaN(p.Y) || double.IsInfinity(p.Y)))
                        {
                            Logger?.LogWarning("      → Skipping triangle with invalid coordinate values");
                            continue;
                        }

                        var poly = new List<double[]>
                        {
                            new[] { vertices[v0].X, vertices[v0].Y },
                            new[] { vertices[v1].X, vertices[v1].Y },
                            new[] { vertices[v2].X, vertices[v2].Y },
                            new[] { vertices[v0].X, vertices[v0].Y }
                        };

                        // Skip duplicate/degenerate polygons
                        if (poly.Distinct(new DoubleArrayComparer()).Count() < 3)
                            continue;

                        result.Polygons.Add(poly);
                    }
                }
                else
                {
                    Logger?.LogWarning("      → Skipping non-mesh face of type: {Type}", face.GetType().Name);
                }
            }
        }

        private static JObject CreateGeoJsonFeature(string id, string type, List<List<double[]>> polys)
        {
            var coords = new JArray();
            foreach (var poly in polys)
            {
                var ring = new JArray();
                foreach (var pt in poly)
                {
                    var (lon, lat) = TransformCoordinates(pt[0], pt[1]);
                    ring.Add(new JArray(lon, lat));
                }
                coords.Add(new JArray(new JArray(ring)));
            }

            return new JObject(
                new JProperty("type", "Feature"),
                new JProperty("id", id),
                new JProperty("properties", new JObject(new JProperty("type", type))),
                new JProperty("geometry", new JObject(
                    new JProperty("type", "MultiPolygon"),
                    new JProperty("coordinates", coords)
                ))
            );
        }

        private static (double lon, double lat) TransformCoordinates(double x, double y)
        {
            double lon = x * CoordinateScale;
            double lat = y * CoordinateScale;
            lon = Math.Clamp(lon, -180, 180);
            lat = Math.Clamp(lat, -90, 90);
            return (lon, lat);
        }

        private class DoubleArrayComparer : IEqualityComparer<double[]>
        {
            public bool Equals(double[]? a, double[]? b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (Math.Abs(a[i] - b[i]) > 1e-9) return false;
                }
                return true;
            }

            public int GetHashCode(double[] obj)
            {
                unchecked
                {
                    int hash = 17;
                    foreach (var val in obj)
                        hash = hash * 31 + val.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
