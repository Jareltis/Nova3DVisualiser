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
        List<Vector2> texcoords = new List<Vector2>();
        List<FacingInfo> faces = new List<FacingInfo>();
        List<Vector2> uvs = new List<Vector2>();   // per-face-corner UVs, parallel to faces (3 per triangle)

        CultureInfo ci = CultureInfo.InvariantCulture;

        // Resolves a 1-based (or negative-relative, or 0 = none) OBJ vt index to an image-space UV with the
        // OBJ->image V-FLIP (v_tex = 1 - v_obj): OBJ texcoords have a bottom-left origin, but Texture stores
        // the top row first. An absent/out-of-range index -> Zero (untextured corner, byte-identical to before).
        Vector2 CornerUv(int idx)
        {
            if (idx <= 0 || idx > texcoords.Count) return Vector2.Zero;
            Vector2 tc = texcoords[idx - 1];
            return new Vector2(tc.X, 1f - tc.Y);
        }

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
            else if (type == "vt")
            {
                float u = float.Parse(parts[1], ci);
                float v = parts.Length > 2 ? float.Parse(parts[2], ci) : 0f;   // optional w (parts[3]) unused
                texcoords.Add(new Vector2(u, v));
            }
            else if (type == "f")
            {
                List<int> faceVerts = new List<int>();
                List<int> faceNorms = new List<int>();
                List<int> faceUvs = new List<int>();
                bool hasNormals = true;

                for (int i = 1; i < parts.Length; i++)
                {
                    string[] tok = parts[i].Split('/');

                    if (!int.TryParse(tok[0], out int vi)) continue;
                    if (vi < 0) vi = vertices.Count + vi + 1;   // negative = relative to current count
                    faceVerts.Add(vi);

                    // UV index tok[1] is optional (empty in "v//vn"); 0 = none. Supports negative-relative.
                    int ti = 0;
                    if (tok.Length >= 2 && int.TryParse(tok[1], out int tParsed))
                        ti = tParsed < 0 ? texcoords.Count + tParsed + 1 : tParsed;
                    faceUvs.Add(ti);

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
                    // Per-face-corner UVs in the SAME fan order as the triangle (v-flipped in CornerUv).
                    uvs.Add(CornerUv(faceUvs[0]));
                    uvs.Add(CornerUv(faceUvs[t]));
                    uvs.Add(CornerUv(faceUvs[t + 1]));
                }
            }
        }

       if (normals.Count == 0)
        {
            normals.Add(new Vector3(0, 1, 0));
        }

        Logger.Info($"Loaded {Path.GetFileName(filePath)}: {vertices.Count} verts, {normals.Count} normals, {texcoords.Count} texcoords, {faces.Count} tris");

        // Only hand UVs to the mesh when the file actually carried texcoords; otherwise null keeps
        // untextured imported meshes byte-identical (Zero UVs), so the untextured gputest scenes stay Δ=0.
        Vector2[]? uvArray = texcoords.Count > 0 ? uvs.ToArray() : null;
        return new Object3d(vertices.ToArray(), normals.ToArray(), faces.ToArray(), uvArray);
    }
}