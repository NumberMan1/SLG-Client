using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Slg
{
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class WorldPreview : MonoBehaviour
    {
        private const float Radius = 0.51f;
        [SerializeField] private Shader surfaceShader;
        private Mesh mesh;
        private Material[] materials;

        private void OnEnable()
        {
            var shader = surfaceShader ? surfaceShader : Shader.Find("Standard");
            if (!shader)
            {
                return;
            }

            var palette = new Color32[]
            {
                new(38, 95, 108, 255), new(65, 121, 126, 255), new(165, 150, 104, 255),
                new(93, 126, 92, 255), new(132, 144, 99, 255), new(71, 102, 79, 255),
                new(117, 124, 112, 255), new(184, 184, 157, 255), new(211, 168, 91, 255)
            };
            materials = new Material[palette.Length];
            for (int i = 0; i < palette.Length; i++)
            {
                materials[i] = new Material(shader) { color = palette[i], hideFlags = HideFlags.HideAndDontSave };
                materials[i].SetFloat("_Glossiness", 0.12f);
            }

            var vertices = new List<Vector3>();
            var faces = new List<int>[palette.Length];
            for (int i = 0; i < faces.Length; i++)
            {
                faces[i] = new List<int>();
            }
            for (int r = -11; r <= 11; r++)
            {
                for (int q = -13; q <= 13; q++)
                {
                    float x = Mathf.Sqrt(3) * 0.52f * (q + r * 0.5f);
                    float z = 0.78f * r;
                    float highland = Mathf.PerlinNoise((x + 33) * 0.23f, (z + 21) * 0.23f);
                    float river = Mathf.Abs(x + 1.4f + Mathf.Sin(z * 0.47f) * 2.2f);
                    int tile = river < 0.8f ? 0 : river < 1.55f ? 1 : river < 2.05f ? 2 :
                        highland > 0.72f ? 7 : highland > 0.6f ? 6 : highland > 0.52f ? 5 :
                        highland > 0.41f ? 3 : 4;
                    float height = tile < 2 ? 0.25f : 0.45f + Mathf.Floor(highland * 7) * 0.18f;
                    AddHex(vertices, faces[tile], new Vector3(x, 0, z), Radius, height);
                    if ((q, r) == (0, -2) || (q, r) == (4, 1) || (q, r) == (-5, 1))
                    {
                        AddHex(vertices, faces[8], new Vector3(x, height, z), 0.24f, 0.16f);
                        AddHex(vertices, faces[2], new Vector3(x, height + 0.16f, z), 0.16f, 0.44f);
                        AddHex(vertices, faces[8], new Vector3(x, height + 0.6f, z), 0.20f, 0.12f);
                    }
                }
            }

            mesh = new Mesh { name = "Frontier terrain", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices);
            mesh.subMeshCount = faces.Length;
            for (int i = 0; i < faces.Length; i++)
            {
                mesh.SetTriangles(faces[i], i);
            }
            mesh.RecalculateNormals();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = GetComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.On;
        }

        private static void AddHex(List<Vector3> vertices, List<int> faces, Vector3 center, float radius, float height)
        {
            var top = center + Vector3.up * height;
            for (int i = 0; i < 6; i++)
            {
                float a = (i * 60 + 30) * Mathf.Deg2Rad;
                float b = a + Mathf.PI / 3;
                var first = radius * new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var second = radius * new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b));
                int index = vertices.Count;
                vertices.Add(top);
                vertices.Add(top + second);
                vertices.Add(top + first);
                faces.Add(index);
                faces.Add(index + 1);
                faces.Add(index + 2);

                index = vertices.Count;
                vertices.Add(top + first);
                vertices.Add(top + second);
                vertices.Add(center + second);
                vertices.Add(center + first);
                faces.Add(index);
                faces.Add(index + 1);
                faces.Add(index + 2);
                faces.Add(index);
                faces.Add(index + 2);
                faces.Add(index + 3);
            }
        }

        private void OnDisable()
        {
            if (!mesh)
            {
                return;
            }
            GetComponent<MeshFilter>().sharedMesh = null;
            GetComponent<MeshRenderer>().sharedMaterials = new Material[0];
            DestroyPreview(mesh);
            foreach (var material in materials)
            {
                DestroyPreview(material);
            }
        }

        private static void DestroyPreview(Object value)
        {
            if (Application.isPlaying)
            {
                Destroy(value);
            }
            else
            {
                DestroyImmediate(value);
            }
        }
    }
}
