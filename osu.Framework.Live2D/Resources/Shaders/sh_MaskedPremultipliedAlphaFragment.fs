#ifndef GL_ES
    #define lowp
    #define mediump
    #define highp
#else
    // GL_ES expects a defined precision for every member. Users may miss this requirement, so a default precision is specified.
    precision mediump float;
#endif

varying vec2 v_texCoord;
varying vec4 v_clipPos;
uniform sampler2D s_texture0;
uniform sampler2D s_texture1;
uniform vec4 u_channelFlag;
uniform vec4 u_baseColor;

void main() {
    vec4 col_forMask = texture2D(s_texture0, v_texCoord) * u_baseColor;
    vec4 clipMask = (1.0 - texture2D(s_texture1, v_clipPos.xy / v_clipPos.w)) * u_channelFlag;

    float maskVal = clipMask.r + clipMask.g + clipMask.b + clipMask.a;
    col_forMask = col_forMask * maskVal;
    gl_FragColor = col_forMask;
}
