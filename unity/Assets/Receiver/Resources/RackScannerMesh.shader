// 스캔 메쉬 표시용. 정점 법선 없이 화면 미분으로 면 법선을 구해 삼각형 단위로 음영을 준다 (면이 얼마나 합쳐졌는지 보이게).
// 색은 면 방향을 섞는다 (바닥·천장 = 초록, 벽 = 빨강/파랑 계열). 양면을 그리고, 와이어프레임 선이 위에 보이도록 면을 살짝 뒤로 민다.
// Built-in / URP 모두에서 동작하는 단순 Unlit 패스. Resources 에 있어 빌드에 포함된다.
Shader "RackScanner/Mesh"
{
    Properties
    {
        _Color ("Color", Color) = (0.85,0.85,0.85,1)
        _NormalTint ("Normal Tint", Range(0,1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            Offset 1, 1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            float _NormalTint;
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
                float3 l = normalize(float3(0.35, 0.85, 0.4));
                float shade = 0.35 + 0.65 * abs(dot(n, l));
                float3 c = lerp(_Color.rgb, abs(n), _NormalTint) * shade;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
