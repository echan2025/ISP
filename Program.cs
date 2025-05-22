using System;
using System.IO;
using Newtonsoft.Json;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.ModelGeometry.Scene;
using System.Collections.Generic;
using System.Linq;

namespace IfcToGeoJsonProcessor
{
    public class GeometryResult
    {
        public List<List<double[]>> Polygons { get; set; } = new();
        public string? Error { get; set; }
    }

    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: IfcToGeoJsonTopFloor <ifcPath> <elementGuid>");
                return 1;
            }

            string ifcPath = args[0];
            string elementGuid = args[1];

            var result = ProcessElement(ifcPath, elementGuid);
            Console.WriteLine(result);
            return 0;
        }

        public static string ProcessElement(string ifcPath, string elementGuid)
        {
            var result = new GeometryResult();

            try
            {
                using var store = IfcStore.Open(ifcPath);

                var element = store.Instances.FirstOrDefault<IIfcProduct>(e => e.GlobalId == elementGuid);
                if (element == null)
                {
                    result.Error = "Element not found";
                    return JsonConvert.SerializeObject(result);
                }

                var context = new Xbim3DModelContext(store);
                context.CreateContext();

                var shapeInstances = context.ShapeInstances()
                    .Where(si => si.IfcProductLabel == element.EntityLabel)
                    .ToList();

                if (!shapeInstances.Any())
                {
                    result.Error = "No geometry found";
                    return JsonConvert.SerializeObject(result);
                }

                foreach (var instance in shapeInstances)
                {
                    var geom = context.ShapeGeometry(instance);
                    if (geom == null) continue;

                    var geometryProperty = geom.GetType().GetProperty("Geometry");
                    if (geometryProperty == null) continue;

                    var mesh = geometryProperty.GetValue(geom);
                    if (mesh == null) continue;

                    var verticesProp = mesh.GetType().GetProperty("Vertices");
                    var facesProp = mesh.GetType().GetProperty("Faces");
                    if (verticesProp == null || facesProp == null) continue;

                    var vertices = verticesProp.GetValue(mesh) as System.Collections.IList;
                    var faces = facesProp.GetValue(mesh) as System.Collections.IEnumerable;
                    if (vertices == null || faces == null) continue;

                    foreach (var face in faces)
                    {
                        var v0Prop = face.GetType().GetProperty("V0");
                        var v1Prop = face.GetType().GetProperty("V1");
                        var v2Prop = face.GetType().GetProperty("V2");
                        if (v0Prop == null || v1Prop == null || v2Prop == null) continue;

                        int v0 = (int)v0Prop.GetValue(face);
                        int v1 = (int)v1Prop.GetValue(face);
                        int v2 = (int)v2Prop.GetValue(face);

                        if (v0 >= 0 && v1 >= 0 && v2 >= 0 &&
                            v0 < vertices.Count && v1 < vertices.Count && v2 < vertices.Count)
                        {
                            var vert0 = vertices[v0];
                            var vert1 = vertices[v1];
                            var vert2 = vertices[v2];

                            double x0 = Convert.ToDouble(vert0.GetType().GetProperty("X").GetValue(vert0));
                            double y0 = Convert.ToDouble(vert0.GetType().GetProperty("Y").GetValue(vert0));
                            double z0 = Convert.ToDouble(vert0.GetType().GetProperty("Z").GetValue(vert0));

                            double x1 = Convert.ToDouble(vert1.GetType().GetProperty("X").GetValue(vert1));
                            double y1 = Convert.ToDouble(vert1.GetType().GetProperty("Y").GetValue(vert1));
                            double z1 = Convert.ToDouble(vert1.GetType().GetProperty("Z").GetValue(vert1));

                            double x2 = Convert.ToDouble(vert2.GetType().GetProperty("X").GetValue(vert2));
                            double y2 = Convert.ToDouble(vert2.GetType().GetProperty("Y").GetValue(vert2));
                            double z2 = Convert.ToDouble(vert2.GetType().GetProperty("Z").GetValue(vert2));

                            result.Polygons.Add(new List<double[]>
                            {
                                new[] { x0, y0, z0 },
                                new[] { x1, y1, z1 },
                                new[] { x2, y2, z2 },
                                new[] { x0, y0, z0 }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = $"Exception: {ex.Message}\n{ex.StackTrace}";
            }

            return JsonConvert.SerializeObject(result, Formatting.Indented);
        }
    }
}
