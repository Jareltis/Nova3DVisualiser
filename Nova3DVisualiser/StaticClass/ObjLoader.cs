using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Shape;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nova3DVisualiser.StaticClass;
public class ObjLoader
{
    public static Object3d Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}");

        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<FacingInfo> faces = new List<FacingInfo>();

        CultureInfo ci = CultureInfo.InvariantCulture;

        foreach (string line in File.ReadAllLines(filePath))
        {
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string type = parts[0];

            if (type == "v")
            {
                float x = float.Parse(parts[1], ci);
                float y = float.Parse(parts[2], ci);
                float z = float.Parse(parts[3], ci);
                vertices.Add(new Vector3(x, y, z));
            }
            else if (type == "vn")
            {
                float x = float.Parse(parts[1], ci);
                float y = float.Parse(parts[2], ci);
                float z = float.Parse(parts[3], ci);
                normals.Add(new Vector3(x, y, z));
            }
            else if (type == "f")
            {
                List<int> faceVerts = new List<int>();
                List<int> faceNorms = new List<int>();
                bool hasNormals = true;

                for (int i = 1; i < parts.Length; i++)
                {
                    string[] tok = parts[i].Split('/');

                    if (!int.TryParse(tok[0], out int vi)) continue;
                    if (vi < 0) vi = vertices.Count + vi + 1;   // negative = relative to current count
                    faceVerts.Add(vi);

                    if (tok.Length >= 3 && int.TryParse(tok[2], out int ni))
                    {
                        if (ni < 0) ni = normals.Count + ni + 1;
                        faceNorms.Add(ni);
                    }
                    else
                    {
                        hasNormals = false;
                    }
                }

                if (faceVerts.Count < 3) continue;

                // No usable per-vertex normals -> one geometric face normal (flat fallback).
                if (!hasNormals || faceNorms.Count != faceVerts.Count)
                {
                    Vector3 a = vertices[faceVerts[0] - 1];
                    Vector3 b = vertices[faceVerts[1] - 1];
                    Vector3 c = vertices[faceVerts[2] - 1];
                    Vector3 geo = Vector3.Cross(b - a, c - a).Norm();
                    normals.Add(geo);
                    int geoIndex = normals.Count; // 1-based
                    faceNorms.Clear();
                    for (int k = 0; k < faceVerts.Count; k++) faceNorms.Add(geoIndex);
                }

                // Fan-triangulate (handles triangles, quads, and n-gons uniformly).
                for (int t = 1; t + 1 < faceVerts.Count; t++)
                {
                    faces.Add(new FacingInfo(
                        new int[] { faceVerts[0], faceVerts[t], faceVerts[t + 1] },
                        new int[] { faceNorms[0], faceNorms[t], faceNorms[t + 1] }
                    ));
                }
            }
        }

       if (normals.Count == 0)
        {
            normals.Add(new Vector3(0, 1, 0));
        }

        Logger.Info($"Loaded {Path.GetFileName(filePath)}: {vertices.Count} verts, {normals.Count} normals, {faces.Count} tris");

        return new Object3d(vertices.ToArray(), normals.ToArray(), faces.ToArray());
    }
}