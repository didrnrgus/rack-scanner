using System.Collections.Generic;
using UnityEngine;

namespace RackScanner.Receiver.View
{
    /// <summary>보조 표시: 마커 원점 축, 바닥 격자. Lines 토폴로지 Mesh 하나로 그린다.</summary>
    public static class SceneHelpers
    {
        public static GameObject CreateAxesAndGrid(Transform parent, float gridHalfSize = 10f, float gridStep = 1f)
        {
            var verts = new List<Vector3>();
            var cols = new List<Color32>();
            void Line(Vector3 a, Vector3 b, Color32 c) { verts.Add(a); verts.Add(b); cols.Add(c); cols.Add(c); }

            var grid = new Color32(70, 70, 70, 255);
            for (float x = -gridHalfSize; x <= gridHalfSize + 1e-3f; x += gridStep)
            {
                Line(new Vector3(x, 0, -gridHalfSize), new Vector3(x, 0, gridHalfSize), grid);
                Line(new Vector3(-gridHalfSize, 0, x), new Vector3(gridHalfSize, 0, x), grid);
            }
            // 마커 원점 축: X 빨강, Y 초록, Z 파랑 (1m)
            Line(Vector3.zero, Vector3.right, new Color32(255, 60, 60, 255));
            Line(Vector3.zero, Vector3.up, new Color32(60, 255, 60, 255));
            Line(Vector3.zero, Vector3.forward, new Color32(60, 120, 255, 255));

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            var idx = new int[verts.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            var go = new GameObject("AxesAndGrid");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = PointCloudView.CreateVertexColorMaterial();
            return go;
        }
    }
}
