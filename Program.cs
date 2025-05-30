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
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => 
            {
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddConsole(options => options.IncludeScopes = true);
            });
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
                Logger?.LogInformation("Starting conversion of {IfcPath} to {OutputPath}", args[0], args[1]);
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

            // Log the product type and representation
            Logger?.LogInformation("  → Product type: {Type}", element.GetType().Name);
            if (element.Representation != null)
            {
                Logger?.LogInformation("  → Representation type: {Type}", element.Representation.GetType().Name);
                foreach (var rep in element.Representation.Representations)
                {
                    Logger?.LogInformation("    → Representation item type: {Type}", rep.GetType().Name);
                    foreach (var item in rep.Items)
                    {
                        Logger?.LogInformation("      → Item type: {Type}", item.GetType().Name);
                    }
                }
            }

            foreach (var inst in instances)
            {
                Logger?.LogInformation("  → Processing shape instance:");
                Logger?.LogInformation("    → Type: {Type}", inst.GetType().Name);
                Logger?.LogInformation("    → Representation type: {Type}", inst.RepresentationType);
                Logger?.LogInformation("    → Style label: {Label}", inst.StyleLabel);
                Logger?.LogInformation("    → Product label: {Label}", inst.IfcProductLabel);
                Logger?.LogInformation("    → Shape geometry label: {Label}", inst.ShapeGeometryLabel);
                ProcessShapeInstance(context, inst, result);
            }

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

            Logger?.LogInformation("    → Geometry details:");
            Logger?.LogInformation("      → Vertices: {Count}", vertices?.Count ?? 0);
            Logger?.LogInformation("      → Faces: {Count}", faces?.Count ?? 0);

            if (vertices?.Count > 0)
            {
                for (int i = 0; i < Math.Min(5, vertices.Count); i++)
                {
                    Logger?.LogInformation("      → Vertex {Index}: X={X}, Y={Y}, Z={Z}", 
                        i, vertices[i].X, vertices[i].Y, vertices[i].Z);
                }
            }

            if (vertices == null || faces == null || vertices.Count == 0)
                return;

            // Project all vertices to 2D using best-fit plane (PCA)
            var projected2D = ProjectToBestFitPlane(vertices);

            foreach (var face in faces)
            {
                Logger?.LogInformation("    → Processing face of type: {Type}", face.GetType().Name);

                if (face is Xbim.Common.Geometry.WexBimMeshFace meshFace)
                {
                    var indices = meshFace.Indices?.ToArray();
                    if (indices == null || indices.Length < 3)
                    {
                        Logger?.LogWarning("      → Skipping face due to insufficient indices");
                        continue;
                    }

                    Logger?.LogInformation("      → Face has {Count} indices", indices.Length);
                    Logger?.LogInformation("      → First few indices: {Indices}", 
                        string.Join(", ", indices.Take(Math.Min(10, indices.Length))));

                    var poly = new List<double[]>();
                    var uniqueIndices = new HashSet<int>();

                    foreach (var index in indices)
                    {
                        if (index < 0 || index >= projected2D.Count)
                        {
                            Logger?.LogWarning("      → Skipping invalid vertex index: {Index}", index);
                            continue;
                        }
                        if (uniqueIndices.Add(index))
                        {
                            var pt2d = projected2D[index];
                            poly.Add(new[] { pt2d[0], pt2d[1] });
                        }
                    }

                    if (poly.Count >= 3)
                    {
                        poly.Add(poly[0]);
                        result.Polygons.Add(poly);
                        Logger?.LogInformation("      → Added projected polygon with {Count} points", poly.Count);
                    }
                    else
                    {
                        Logger?.LogWarning("      → Skipping face with insufficient unique points: {Count}", poly.Count);
                    }
                }
                else
                {
                    Logger?.LogWarning("      → Skipping non-mesh face of type: {Type}", face.GetType().Name);
                }
            }

            Logger?.LogInformation("    → Total polygons created: {Count}", result.Polygons.Count);
        }

        // Helper: Project 3D points to best-fit plane using PCA
        private static List<double[]> ProjectToBestFitPlane(List<XbimPoint3D> points)
        {
            // Compute centroid
            double cx = points.Average(p => p.X);
            double cy = points.Average(p => p.Y);
            double cz = points.Average(p => p.Z);
            var centered = points.Select(p => new[] { p.X - cx, p.Y - cy, p.Z - cz }).ToList();

            // Compute covariance matrix
            double[,] cov = new double[3, 3];
            foreach (var v in centered)
            {
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        cov[i, j] += v[i] * v[j];
            }
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    cov[i, j] /= points.Count;

            // Eigen decomposition (find principal axes)
            // We'll use a simple power iteration for the largest two eigenvectors
            var pc1 = PowerIteration(cov, 100);
            var cov2 = SubtractOuterProduct(cov, pc1);
            var pc2 = PowerIteration(cov2, 100);

            // Project each point onto the first two principal axes
            var projected = new List<double[]>();
            foreach (var v in centered)
            {
                double x = Dot(v, pc1);
                double y = Dot(v, pc2);
                projected.Add(new[] { x, y });
            }
            return projected;
        }

        // Helper: Power iteration to find dominant eigenvector
        private static double[] PowerIteration(double[,] m, int steps)
        {
            var v = new double[] { 1, 0, 0 };
            for (int s = 0; s < steps; s++)
            {
                var mv = new double[3];
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        mv[i] += m[i, j] * v[j];
                double norm = Math.Sqrt(mv[0] * mv[0] + mv[1] * mv[1] + mv[2] * mv[2]);
                for (int i = 0; i < 3; i++)
                    v[i] = mv[i] / norm;
            }
            return v;
        }

        // Helper: Subtract outer product of a vector from a matrix
        private static double[,] SubtractOuterProduct(double[,] m, double[] v)
        {
            var result = (double[,])m.Clone();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    result[i, j] -= v[i] * v[j];
            return result;
        }

        // Helper: Dot product
        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
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
                coords.Add(ring);
            }

            return new JObject(
                new JProperty("type", "Feature"),
                new JProperty("id", id),
                new JProperty("properties", new JObject(new JProperty("type", type))),
                new JProperty("geometry", new JObject(
                    new JProperty("type", "Polygon"),
                    new JProperty("coordinates", new JArray(coords))
                ))
            );
        }

        private static (double lon, double lat) TransformCoordinates(double x, double y)
        {
            // Remove the scaling factor for now to see the actual coordinates
            double lon = x;
            double lat = y;
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

        private static void ProcessSweptDiskSolid(List<XbimPoint3D> vertices, GeometryResult result)
        {
            Logger?.LogInformation("Processing swept disk solid with {VertexCount} vertices", vertices.Count);

            // Instead of creating circles, we'll use the actual mesh vertices
            // Group vertices by their Z coordinate to find cross-sections
            var crossSections = vertices
                .GroupBy(v => Math.Round(v.Z, 6))
                .OrderBy(g => g.Key)
                .Select(g => g.ToList())
                .ToList();

            Logger?.LogInformation("Found {SectionCount} cross-sections", crossSections.Count);

            if (crossSections.Count < 2)
            {
                Logger?.LogWarning("Not enough cross-sections to create a swept path");
                return;
            }

            // Create polygons for each cross-section
            foreach (var section in crossSections)
            {
                if (section.Count < 3)
                {
                    Logger?.LogWarning("Cross-section has too few points: {Count}", section.Count);
                    continue;
                }

                var poly = new List<double[]>();
                foreach (var point in section)
                {
                    poly.Add(new[] { point.X, point.Y });
                }
                // Close the polygon
                poly.Add(new[] { section[0].X, section[0].Y });
                result.Polygons.Add(poly);
            }

            // Create connecting faces between cross-sections
            for (int i = 0; i < crossSections.Count - 1; i++)
            {
                var currentSection = crossSections[i];
                var nextSection = crossSections[i + 1];

                // Create triangular faces between sections
                for (int j = 0; j < currentSection.Count; j++)
                {
                    int nextJ = (j + 1) % currentSection.Count;
                    var poly = new List<double[]>
                    {
                        new[] { currentSection[j].X, currentSection[j].Y },
                        new[] { nextSection[j].X, nextSection[j].Y },
                        new[] { nextSection[nextJ].X, nextSection[nextJ].Y },
                        new[] { currentSection[nextJ].X, currentSection[nextJ].Y },
                        new[] { currentSection[j].X, currentSection[j].Y }
                    };
                    result.Polygons.Add(poly);
                }
            }

            Logger?.LogInformation("Created {PolygonCount} polygons", result.Polygons.Count);
        }
    }
}
