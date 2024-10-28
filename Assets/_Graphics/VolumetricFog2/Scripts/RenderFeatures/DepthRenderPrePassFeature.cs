using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

namespace VolumetricFogAndMist2
{
    public class DepthRenderPrePassFeature : ScriptableRendererFeature
    {

        public class DepthRenderPass : ScriptableRenderPass
        {

            public static readonly List<Renderer> cutOutRenderers = new List<Renderer>();
            public static int transparentLayerMask;
            public static int alphaCutoutLayerMask;

            const string m_ProfilerTag = "CustomDepthPrePass";
            const string m_DepthOnlyShader = "Hidden/VolumetricFog2/DepthOnly";

            FilteringSettings m_FilteringSettings;
            int currentCutoutLayerMask;
            readonly List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();

            RTHandle m_Depth;
            Material depthOnlyMaterial, depthOnlyMaterialCutOff;
            Material[] depthOverrideMaterials;

            public DepthRenderPass()
            {
                m_ShaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                m_ShaderTagIdList.Add(new ShaderTagId("LightweightForward"));
                m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent, 0);
                SetupKeywords();
                FindAlphaClippingRenderers();
            }

            void SetupKeywords()
            {
                if (transparentLayerMask != 0 || alphaCutoutLayerMask != 0)
                {
                    Shader.EnableKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                }
                else
                {
                    Shader.DisableKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                }
            }

            public static void SetupLayerMasks(int transparentLayerMask, int alphaCutoutLayerMask)
            {
                DepthRenderPass.transparentLayerMask = transparentLayerMask;
                DepthRenderPass.alphaCutoutLayerMask = alphaCutoutLayerMask;
                if (alphaCutoutLayerMask != 0)
                {
                    FindAlphaClippingRenderers();
                }
            }

            public static void FindAlphaClippingRenderers()
            {
                cutOutRenderers.Clear();
                if (alphaCutoutLayerMask == 0) return;
                Renderer[] rr = FindObjectsByType<Renderer>(FindObjectsSortMode.None); // Zast�pienie przestarza�ej metody
                for (int r = 0; r < rr.Length; r++)
                {
                    if (((1 << rr[r].gameObject.layer) & alphaCutoutLayerMask) != 0)
                    {
                        cutOutRenderers.Add(rr[r]);
                    }
                }
            }


            [System.Obsolete("This Configure method overrides an obsolete member.")]
            public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
            {
                if (transparentLayerMask != m_FilteringSettings.layerMask || alphaCutoutLayerMask != currentCutoutLayerMask)
                {
                    m_FilteringSettings = new FilteringSettings(RenderQueueRange.transparent, transparentLayerMask);
                    currentCutoutLayerMask = alphaCutoutLayerMask;
                    SetupKeywords();
                }
                RenderTextureDescriptor depthDesc = cameraTextureDescriptor;
                depthDesc.colorFormat = RenderTextureFormat.Depth;
                depthDesc.depthBufferBits = 32;
                depthDesc.msaaSamples = 1;

                m_Depth = RTHandles.Alloc(depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_CustomDepthTexture");
                cmd.SetGlobalTexture(ShaderParams.CustomDepthTexture, m_Depth.nameID);
                ConfigureTarget(m_Depth);  // U�ycie RTHandle tutaj
                ConfigureClear(ClearFlag.All, Color.black);
            }

            [System.Obsolete("This Execute method overrides an obsolete member.")]
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (transparentLayerMask == 0 && alphaCutoutLayerMask == 0) return;
                CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                if (alphaCutoutLayerMask != 0)
                {
                    VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
                    if (manager != null)
                    {
                        if (depthOnlyMaterialCutOff == null)
                        {
                            Shader depthOnlyCutOff = Shader.Find(m_DepthOnlyShader);
                            depthOnlyMaterialCutOff = new Material(depthOnlyCutOff);
                        }
                        int renderersCount = cutOutRenderers.Count;
                        if (depthOverrideMaterials == null || depthOverrideMaterials.Length < renderersCount)
                        {
                            depthOverrideMaterials = new Material[renderersCount];
                        }
                        for (int k = 0; k < renderersCount; k++)
                        {
                            Renderer renderer = cutOutRenderers[k];
                            if (renderer != null && renderer.isVisible)
                            {
                                Material mat = renderer.sharedMaterial;
                                if (mat != null)
                                {
                                    if (depthOverrideMaterials[k] == null)
                                    {
                                        depthOverrideMaterials[k] = Instantiate(depthOnlyMaterialCutOff);
                                        depthOverrideMaterials[k].EnableKeyword(ShaderParams.SKW_CUSTOM_DEPTH_ALPHA_TEST);
                                    }
                                    Material overrideMaterial = depthOverrideMaterials[k];
                                    overrideMaterial.SetFloat(ShaderParams.CustomDepthAlphaCutoff, manager.alphaCutOff);
                                    if (mat.HasProperty(ShaderParams.CustomDepthBaseMap))
                                    {
                                        overrideMaterial.SetTexture(ShaderParams.MainTex, mat.GetTexture(ShaderParams.CustomDepthBaseMap));
                                    }
                                    else if (mat.HasProperty(ShaderParams.MainTex))
                                    {
                                        overrideMaterial.SetTexture(ShaderParams.MainTex, mat.GetTexture(ShaderParams.MainTex));
                                    }
                                    cmd.DrawRenderer(renderer, overrideMaterial);
                                }
                            }
                        }
                    }
                }

                if (transparentLayerMask != 0)
                {
                    SortingCriteria sortingCriteria = SortingCriteria.CommonTransparent;
                    var drawSettings = CreateDrawingSettings(m_ShaderTagIdList, ref renderingData, sortingCriteria);
                    drawSettings.perObjectData = PerObjectData.None;
                    if (depthOnlyMaterial == null)
                    {
                        Shader depthOnly = Shader.Find(m_DepthOnlyShader);
                        depthOnlyMaterial = new Material(depthOnly);
                    }
                    drawSettings.overrideMaterial = depthOnlyMaterial;
                    context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);
                }

                context.ExecuteCommandBuffer(cmd);

                CommandBufferPool.Release(cmd);
            }

            public override void FrameCleanup(CommandBuffer cmd)
            {
                if (cmd == null) return;
                RTHandles.Release(m_Depth); // Zwalnianie RTHandle zamiast RenderTargetHandle
            }
        }

        DepthRenderPass m_ScriptablePass;
        public static bool installed;

        public override void Create()
        {
            m_ScriptablePass = new DepthRenderPass()
            {
                // Configures where the render pass should be injected.
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        void OnDestroy()
        {
            installed = false;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            installed = true;
            renderer.EnqueuePass(m_ScriptablePass);
        }

    }
}
