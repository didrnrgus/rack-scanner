// 정점 색을 그대로 그리는 무조명 셰이더 (점군 Mesh 의 Points 토폴로지, 보조선 Lines 토폴로지용).
// Built-in / URP 모두에서 동작하는 단순 Unlit 패스. Resources 에 있어 빌드에 포함된다.
Shader "RackScanner/PointCloud"
{
    Properties { _Tint ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float size : PSIZE; };
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Tint;
                o.size = 2.0; // Metal/GL 에서만 적용. DX11 은 항상 1px (TODO: 굵은 점은 GraphicsBuffer 빌보드로)
                return o;
            }
            fixed4 frag (v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
