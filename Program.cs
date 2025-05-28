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
        private static readonly ILoggerFactory LoggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());
        private static readonly ILogger Logger = LoggerFactory.CreateLogger<Program>();

        private const double CoordinateScale = 0.00001;

        static int Main(string[] args)
        {
            XbimServices.Current.ConfigureServices(services =>
                services.AddXbimToolkit(config => config.AddLoggerFactory(LoggerFactory)));

            if (args.Length < 2)
            {
                Logger.LogError("Usage: IfcToGeoJson <ifcPath> <outputPath> [elementGuid]");
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
                Logger.LogInformation("Successfully created GeoJSON at: {Path}", outputPath);
                return 0;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Conversion failed");
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
            context.CreateContext();

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
            context.CreateContext();

            int productCount = store.Instances
                .OfType<IIfcProduct>()
                .Count(p => p.Representation != null);
            Logger.LogInformation("Found {Count} products with geometry", productCount);

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

            Logger.LogInformation("  → Product {Id} has {N} shape instances",
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
                Logger.LogWarning("    → No geometry object for label {Label}", instance.ShapeGeometryLabel);
                return;
            }

            var geometry = geom.GetType().GetProperty("Geometry")?.GetValue(geom);
            if (geometry == null)
            {
                Logger.LogWarning("    → Geometry property null for label {Label}", instance.ShapeGeometryLabel);
                return;
            }

            var vertices = geometry.GetType().GetProperty("Vertices")?.GetValue(geometry) as System.Collections.IList;
            var faces = geometry.GetType().GetProperty("Faces")?.GetValue(geometry) as System.Collections.IEnumerable;

            if (vertices == null || faces == null || vertices.Count == 0)
                return;

            int vCount = vertices.Count;

            foreach (var face in faces.Cast<object>())
            {
                int? v0 = GetVertexIndex(face, "V0", vCount);
                int? v1 = GetVertexIndex(face, "V1", vCount);
                int? v2 = GetVertexIndex(face, "V2", vCount);
                if (!v0.HasValue || !v1.HasValue || !v2.HasValue)
                    continue;

                var poly = new List<double[]>();
                AddVertex(poly, vertices[v0.Value]);
                AddVertex(poly, vertices[v1.Value]);
                AddVertex(poly, vertices[v2.Value]);
                if (poly.Count > 0)
                {
                    poly.Add(poly[0]);
                    result.Polygons.Add(poly);
                }
            }
        }

        private static int? GetVertexIndex(object face, string prop, int max)
        {
            var p = face.GetType().GetProperty(prop);
            var val = p?.GetValue(face) as int?;
            return (val >= 0 && val < max) ? val : null;
        }

        private static void AddVertex(List<double[]> poly, object? vert)
        {
            if (vert == null) return;
            double? Get(string n) => (double?)vert.GetType()
                                                  .GetProperty(n)
                                                  ?.GetValue(vert);
            var x = Get("X");
            var y = Get("Y");
            var z = Get("Z");
            if (x.HasValue && y.HasValue && z.HasValue)
                poly.Add(new[] { x.Value, y.Value, z.Value });
        }

        private static JObject CreateGeoJsonFeature(
            string id, string type, List<List<double[]>> polys)
        {
            var coords = new JArray();
            foreach (var poly in polys)
            {
                var ring = new JArray();
                foreach (var pt in poly)
                {
                    var (lon, lat) = TransformCoordinates(pt[0], pt[1]);
                    ring.Add(new JArray(lon, lat, pt[2]));
                }
                coords.Add(ring);
            }

            return new JObject(
                new JProperty("type", "Feature"),
                new JProperty("id", id),
                new JProperty("properties", new JObject(new JProperty("type", type))),
                new JProperty("geometry", new JObject(
                    new JProperty("type", coords.Count == 1 ? "Polygon" : "MultiPolygon"),
                    new JProperty("coordinates",
                                 coords.Count == 1 ? coords[0] : coords)
                ))
            );
        }

        private static (double lon, double lat) TransformCoordinates(double x, double y)
        {
            double lon = x * CoordinateScale;
            lon = (lon % 360 + 540) % 360 - 180;
            double lat = y * CoordinateScale;
            lat = (lat % 180 + 270) % 180 - 90;
            return (lon, lat);
        }
    }
}
