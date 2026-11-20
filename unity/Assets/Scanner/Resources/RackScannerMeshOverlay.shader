// 아이패드 화면에 ARKit 메쉬를 반투명으로 겹쳐 그린다 (어디까지 찍혔는지 확인용).
// 정점 법선 없이 화면 미분으로 면 법선을 구해 삼각형마다 다른 밝기로 보이게 하고, 법선 방향으로 색을 입힌다
// (바닥·천장 = 초록, 벽 = 빨강/파랑 계열). Built-in / URP 모두에서 동작하는 단순 Unlit 패스. Resources 에 있어 빌드에 포함된다.
Shader "RackScanner/MeshOverlay"
{
    Properties { _Alpha ("Alpha", Range(0,1)) = 0.45 }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Alpha;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; };
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(cross(ddy(i.wpos), ddx(i.wpos)));
                float3 c = abs(n) * 0.8 + 0.2;
                float shade = 0.55 + 0.45 * abs(dot(n, normalize(float3(0.3, 0.8, 0.5))));
                return fixed4(c.r * shade, c.g * shade, c.b * shade, _Alpha);
            }
            ENDCG
        }
    }
}
