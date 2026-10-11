struct SceneLight { vec4 positionRange; vec4 colorIntensity; vec4 directionOuter; vec4 innerType; };
layout(set = 0, binding = 0, std140) uniform SceneData {
    mat4 viewproj;
    mat4 sunViewProjection;
    vec4 cameraPosition;
    vec4 sunlightDirection;
    vec4 sunlightColor;
    vec4 ambientColor;
    vec4 shadowSettings;
    vec4 cityLights[10];
    vec4 nightSettings;
    vec4 moonLight;
    vec4 lightInfo;
    SceneLight lights[128];
} scene;
layout(set = 0, binding = 1) uniform sampler2DShadow shadowMap;
