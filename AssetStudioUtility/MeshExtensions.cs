using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Wavefront OBJ export for <see cref="Mesh"/>.
    ///
    /// Ported from AssetStudioCLI/Exporter.cs (ExportMesh), which kept it private to the CLI.
    /// Living here makes it available to the GUI and to the Android app as well, and it has no
    /// dependency on the FBX path -- everything it needs comes out of Mesh.ProcessData().
    /// </summary>
    public static class MeshExtensions
    {
        /// <summary>
        /// Writes the mesh as a .obj file. Returns false (writing nothing) when the mesh carries
        /// no usable geometry.
        /// </summary>
        public static bool ExportObj(this Mesh mesh, string path)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            mesh.ProcessData();

            if (mesh.m_VertexCount <= 0) return false;
            if (mesh.m_Vertices == null || mesh.m_Vertices.Length == 0) return false;
            if (mesh.m_Indices == null || mesh.m_Indices.Count == 0) return false;

            var sb = new StringBuilder();
            // InvariantCulture throughout: the original used plain AppendFormat, which emits a
            // comma as the decimal separator under a locale like de-DE and silently produces an
            // obj no importer can read.
            var inv = CultureInfo.InvariantCulture;
            sb.Append("g ").Append(mesh.m_Name).Append('\n');

            // Vertices. Unity may store 3 or 4 components per vertex depending on platform.
            var vc = mesh.m_Vertices.Length == mesh.m_VertexCount * 4 ? 4 : 3;
            for (var v = 0; v < mesh.m_VertexCount; v++)
            {
                sb.Append("v ")
                  .Append((-mesh.m_Vertices[v * vc] + 0f).ToString(inv)).Append(' ')
                  .Append(mesh.m_Vertices[v * vc + 1].ToString(inv)).Append(' ')
                  .Append(mesh.m_Vertices[v * vc + 2].ToString(inv)).Append('\n');
            }

            // UV0. Can be 2, 3 or 4 components per vertex depending on platform.
            if (mesh.m_UV0 != null && mesh.m_UV0.Length > 0)
            {
                var uc = 4;
                if (mesh.m_UV0.Length == mesh.m_VertexCount * 2) uc = 2;
                else if (mesh.m_UV0.Length == mesh.m_VertexCount * 3) uc = 3;

                for (var v = 0; v < mesh.m_VertexCount; v++)
                {
                    sb.Append("vt ")
                      .Append(mesh.m_UV0[v * uc].ToString(inv)).Append(' ')
                      .Append(mesh.m_UV0[v * uc + 1].ToString(inv)).Append('\n');
                }
            }

            // Normals. Same 3-or-4 ambiguity as vertices.
            if (mesh.m_Normals != null && mesh.m_Normals.Length > 0)
            {
                var nc = 3;
                if (mesh.m_Normals.Length == mesh.m_VertexCount * 4) nc = 4;

                for (var v = 0; v < mesh.m_VertexCount; v++)
                {
                    sb.Append("vn ")
                      .Append((-mesh.m_Normals[v * nc] + 0f).ToString(inv)).Append(' ')
                      .Append(mesh.m_Normals[v * nc + 1].ToString(inv)).Append(' ')
                      .Append(mesh.m_Normals[v * nc + 2].ToString(inv)).Append('\n');
                }
            }

            // Faces, one group per submesh. obj indices are 1-based and wound the other way
            // round compared to Unity's.
            var sum = 0;
            for (var i = 0; i < mesh.m_SubMeshes.Count; i++)
            {
                sb.Append("g ").Append(mesh.m_Name).Append('_').Append(i).Append('\n');

                var indexCount = (int)mesh.m_SubMeshes[i].indexCount;
                var end = sum + indexCount / 3;
                for (var f = sum; f < end; f++)
                {
                    sb.Append("f ")
                      .Append((mesh.m_Indices[f * 3 + 2] + 1).ToString(inv)).Append('/').Append(mesh.m_Indices[f * 3 + 2] + 1).Append('/').Append(mesh.m_Indices[f * 3 + 2] + 1).Append(' ')
                      .Append((mesh.m_Indices[f * 3 + 1] + 1).ToString(inv)).Append('/').Append(mesh.m_Indices[f * 3 + 1] + 1).Append('/').Append(mesh.m_Indices[f * 3 + 1] + 1).Append(' ')
                      .Append((mesh.m_Indices[f * 3] + 1).ToString(inv)).Append('/').Append(mesh.m_Indices[f * 3] + 1).Append('/').Append(mesh.m_Indices[f * 3] + 1).Append('\n');
                }

                sum = end;
            }

            // ProcessData can leave NaNs behind for degenerate meshes; obj importers reject them.
            var text = sb.Replace("NaN", "0").ToString();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(false));

            return true;
        }
    }
}
