using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.ModelGeometry.Scene;
using Xbim.Common.Geometry;

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

        // Configuration
        private const double CoordinateScale = 0.00001;
        private const double DefaultLon = -0.127758;
        private const double DefaultLat = 51.507351;

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Logger.LogError("Usage: IfcToGeoJson <ifcPath> <outputPath> [elementGuid]");
                return 1;
            }

            try
            {
                string geojson;
                if (args.Length > 2)
                {
                    Logger.LogInformation("Processing single element: {Guid}", args[2]);
                    geojson = ProcessSingleElement(args[0], args[2]);
                }
                else
                {
                    Logger.LogInformation("Processing all elements");
                    geojson = ProcessAllElements(args[0]);
                }

                File.WriteAllText(args[1], geojson);
                Logger.LogInformation("Successfully created GeoJSON at: {Path}", args[1]);
                return 0;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Conversion failed");
                File.WriteAllText(args[1], CreateErrorGeoJson(ex));
                return 1;
            }
        }

        private static string CreateErrorGeoJson(Exception ex)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "FeatureCollection",
                features = new object[0],
                error = ex.Message,
                stackTrace = ex.StackTrace
            }, Formatting.Indented);
        }

        public static string ProcessSingleElement(string ifcPath, string elementGuid)
        {
            using var store = IfcStore.Open(ifcPath);
            var element = store.Instances.FirstOrDefault<IIfcProduct>(e => e.GlobalId == elementGuid);
            
            if (element == null)
            {
                throw new Exception($"Element with GUID {elementGuid} not found");
            }

            var context = new Xbim3DModelContext(store);
            context.CreateContext();

            var result = GetElementGeometry(store, context, element);
            return CreateGeoJson(result, elementGuid);
        }

        public static string ProcessAllElements(string ifcPath)
        {
            using var store = IfcStore.Open(ifcPath);
            var context = new Xbim3DModelContext(store);
            context.CreateContext();

            var features = new List<JObject>();
            foreach (var product in store.Instances.OfType<IIfcProduct>().Where(p => p.Representation != null))
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

        private static GeometryResult GetElementGeometry(IfcStore store, Xbim3DModelContext context, IIfcProduct element)
        {
            var result = new GeometryResult();

            try
            {
                foreach (var instance in context.ShapeInstances()
                    .Where(si => si.IfcProductLabel == element.EntityLabel))
                {
                    ProcessShapeInstance(context, instance, result);
                }
            }
            catch (Exception ex)
            {
                result.Error = $"Error processing geometry: {ex.Message}";
                Logger.LogError(ex, "Error processing element {Guid}", element.GlobalId);
            }

            return result;
        }

        private static void ProcessShapeInstance(Xbim3DModelContext context, IXbimShapeInstanceData instance, GeometryResult result)
        {
            var geom = context.ShapeGeometry(instance);
            if (geom == null) return;

            // Get geometry through reflection
            var geometry = geom.GetType().GetProperty("Geometry")?.GetValue(geom);
            if (geometry == null) return;

            var vertices = geometry.GetType().GetProperty("Vertices")?.GetValue(geometry) as System.Collections.IList;
            var faces = geometry.GetType().GetProperty("Faces")?.GetValue(geometry) as System.Collections.IEnumerable;

            if (vertices == null || faces == null) return;

            foreach (var face in faces)
            {
                if (face == null) continue;
                
                var v0 = GetVertexIndex(face, "V0", vertices.Count);
                var v1 = GetVertexIndex(face, "V1", vertices.Count);
                var v2 = GetVertexIndex(face, "V2", vertices.Count);

                if (!v0.HasValue || !v1.HasValue || !v2.HasValue) continue;

                var polygon = new List<double[]>();
                AddVertex(polygon, vertices[v0.Value]);
                AddVertex(polygon, vertices[v1.Value]);
                AddVertex(polygon, vertices[v2.Value]);
                
                if (polygon.Count > 0)
                {
                    polygon.Add(polygon[0]); // Close the polygon
                    result.Polygons.Add(polygon);
                }
            }
        }

        private static string CreateGeoJson(GeometryResult result, string elementGuid)
        {
            if (!result.Polygons.Any())
            {
                return JsonConvert.SerializeObject(new { error = "No geometry data available" });
            }

            var feature = CreateGeoJsonFeature(
                elementGuid,
                "IFC Element",
                result.Polygons
            );

            return feature.ToString(Formatting.Indented);
        }

        private static int? GetVertexIndex(object face, string propName, int vertexCount)
        {
            var prop = face.GetType().GetProperty(propName);
            if (prop == null) return null;
            
            var value = prop.GetValue(face) as int?;
            return value >= 0 && value < vertexCount ? value : null;
        }

        private static void AddVertex(List<double[]> polygon, object? vertex)
        {
            if (vertex == null) return;

            double? GetCoord(string name) => 
                vertex.GetType().GetProperty(name)?.GetValue(vertex) as double?;

            var x = GetCoord("X");
            var y = GetCoord("Y");
            var z = GetCoord("Z");

            if (x.HasValue && y.HasValue && z.HasValue)
                polygon.Add(new[] { x.Value, y.Value, z.Value });
        }

        private static JObject CreateGeoJsonFeature(string id, string type, List<List<double[]>> polygons)
        {
            var coordinates = new JArray();

            foreach (var polygon in polygons)
            {
                var ring = new JArray();
                foreach (var point in polygon)
                {
                    var (lon, lat) = TransformCoordinates(point[0], point[1]);
                    ring.Add(new JArray(lon, lat, point[2]));
                }
                coordinates.Add(ring);
            }

            return new JObject(
                new JProperty("type", "Feature"),
                new JProperty("id", id),
                new JProperty("properties", new JObject(
                    new JProperty("type", type))),
                new JProperty("geometry", new JObject(
                    new JProperty("type", coordinates.Count == 1 ? "Polygon" : "MultiPolygon"),
                    new JProperty("coordinates", coordinates.Count == 1 ? coordinates[0] : coordinates)))
            );
        }

        private static (double lon, double lat) TransformCoordinates(double x, double y)
        {
            double lon = x * CoordinateScale;
            double lat = y * CoordinateScale;

            // Normalize to valid ranges
            lon = (lon % 360 + 540) % 360 - 180; // -180 to 180
            lat = (lat % 180 + 270) % 180 - 90;   // -90 to 90

            return (lon, lat);
        }
    }
}
