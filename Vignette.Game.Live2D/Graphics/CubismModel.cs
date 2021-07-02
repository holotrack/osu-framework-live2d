// Copyright 2020 - 2021 Vignette Project
// Licensed under MIT. See LICENSE for details.
// This software implements Live2D. Copyright (c) Live2D Inc. All Rights Reserved.
// License for Live2D can be found here: http://live2d.com/eula/live2d-open-software-license-agreement_en.html

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Allocation;
using osu.Framework.Extensions.EnumExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osuTK;
using Vignette.Game.Live2D.Extensions;
using Vignette.Game.Live2D.Graphics.Controllers;
using Vignette.Game.Live2D.IO;
using Vignette.Game.Live2D.IO.Serialization;
using Vignette.Game.Live2D.IO.Serialization.Converters;
using Vignette.Game.Live2D.Model;

namespace Vignette.Game.Live2D.Graphics
{
    /// <summary>
    /// A drawable that renders a Live2D model.
    /// </summary>
    [Cached]
    [Cached(typeof(ICubismResourceProvider))]
    public unsafe class CubismModel : CompositeDrawable, ICubismResourceProvider
    {
        private readonly CubismMoc moc;
        private readonly IntPtr buffer;
        private readonly IntPtr handle;

        private readonly CubismRenderer renderer;

        private Task updateTask;
        private CancellationTokenSource updateTaskCancellationToken;

        [Resolved]
        private GameHost host { get; set; }

        /// <summary>
        /// The moc file version of this model. See <see cref="CubismMocVersion"/> for valid values.
        /// </summary>
        public CubismMocVersion Version => moc.Version;

        /// <summary>
        /// A collection of drawables used to draw this model.
        /// </summary>
        public IEnumerable<CubismDrawable> Drawables => drawables;

        /// <summary>
        /// A collection of parameters used to manipulate the model.
        /// </summary>
        public IReadOnlyList<CubismParameter> Parameters => parameters;

        /// <summary>
        /// A collection of parts used to manipulate groups of the model.
        /// </summary>
        public IReadOnlyList<CubismPart> Parts => parts;

        internal IReadOnlyList<MaskingContext> MaskingContexts => maskingContexts;

        /// <summary>
        /// Create a new <see cref="CubismModel"/> with the provided resource store.
        /// </summary>
        /// <param name="resources">The resource store that contains files necessary to build a model.</param>
        public CubismModel(IResourceStore<byte[]> resources)
        {
            this.resources = resources;

            var modelSettingFile = this.resources.GetAvailableResources().FirstOrDefault(s => s.EndsWith("model3.json"));

            if (string.IsNullOrEmpty(modelSettingFile))
                throw new FileNotFoundException($"{nameof(resources)} must contain a model setting file.");

            modelSetting = readJsonSetting<CubismModelSetting>(modelSettingFile);

            using var mocStream = resources.GetStream(modelSetting.FileReferences.Moc);

            if (mocStream == null)
                throw new FileNotFoundException($"{nameof(resources)} does not contain a moc file.");

            moc = new CubismMoc(mocStream);

            int size = CubismCore.csmGetSizeofModel(moc.Handle);
            buffer = Marshal.AllocHGlobal(size + CubismCore.ALIGN_OF_MODEL - 1);
            handle = CubismCore.csmInitializeModelInPlace(moc.Handle, buffer.Align(CubismCore.ALIGN_OF_MODEL), size);

            AddInternal(renderer = new CubismRenderer(this));

            initializeParameters();
            initializeParts();
            initializeDrawables();

            Add(new CubismBreathController());

            if (!string.IsNullOrEmpty(modelSetting.FileReferences.Physics))
                Add(new CubismPhysicsController(readJsonSetting<CubismPhysicsSetting>(modelSetting.FileReferences.Physics)));

            var eyeblinkGroup = modelSetting.Groups.FirstOrDefault(g => g.Name == "EyeBlink");
            if (eyeblinkGroup != null)
                Add(new CubismEyeblinkController(parameters.Where(p => eyeblinkGroup.Ids.Contains(p.Name))));
        }

        private T readJsonSetting<T>(string path) where T : ICubismJsonSetting
        {
            using var stream = resources.GetStream(path);
            using var reader = new StreamReader(stream);
            return JsonConvert.DeserializeObject<T>(reader.ReadToEnd(), new JsonVector2Converter());
        }

        #region Drawable Management

        private readonly List<CubismDrawable> drawables = new List<CubismDrawable>();
        private readonly List<MaskingContext> maskingContexts = new List<MaskingContext>();
        private int* vertexCounts;

        private void initializeDrawables()
        {
            vertexCounts = CubismCore.csmGetDrawableVertexCounts(handle);

            int count = CubismCore.csmGetDrawableCount(handle);
            var names = CubismCore.csmGetDrawableIds(handle);

            var vertexUvs = CubismCore.csmGetDrawableVertexUvs(handle);
            var indexCounts = CubismCore.csmGetDrawableIndexCounts(handle);
            var indices = CubismCore.csmGetDrawableIndices(handle);
            var constantFlags = CubismCore.csmGetDrawableConstantFlags(handle);

            for (int i = 0; i < count; i++)
            {
                var flags = constantFlags[i];
                var drawable = new CubismDrawable
                {
                    ID = i,
                    Name = Marshal.PtrToStringAnsi((IntPtr)names[i]),
                    Indices = pointerToArray<short>(indices[i], indexCounts[i]),
                    IsDoubleSided = flags.HasFlagFast(CubismConstantFlags.IsDoubleSided),
                    IsInvertedMask = flags.HasFlagFast(CubismConstantFlags.IsInvertedMask),
                    TexturePositions = pointerToArray<Vector2>(vertexUvs[i], vertexCounts[i]),
                };

                if (flags.HasFlagFast(CubismConstantFlags.BlendAdditive))
                {
                    drawable.Blending = new BlendingParameters
                    {
                        Source = BlendingType.One,
                        Destination = BlendingType.One,
                        SourceAlpha = BlendingType.Zero,
                        DestinationAlpha = BlendingType.One,
                    };
                }
                else if (flags.HasFlagFast(CubismConstantFlags.BlendMultiplicative))
                {
                    drawable.Blending = new BlendingParameters
                    {
                        Source = BlendingType.DstColor,
                        Destination = BlendingType.OneMinusSrcAlpha,
                        SourceAlpha = BlendingType.Zero,
                        DestinationAlpha = BlendingType.One,
                    };
                }
                else if (flags.HasFlagFast(CubismConstantFlags.BlendNormal))
                {
                    drawable.Blending = new BlendingParameters
                    {
                        Source = BlendingType.One,
                        Destination = BlendingType.OneMinusSrcAlpha,
                        SourceAlpha = BlendingType.One,
                        DestinationAlpha = BlendingType.OneMinusSrcAlpha,
                    };
                }

                drawable.ColourChanged += () => renderer.Invalidate(Invalidation.DrawNode);

                drawables.Add(drawable);
            }

            var masks = CubismCore.csmGetDrawableMasks(handle);
            var maskCounts = CubismCore.csmGetDrawableMaskCounts(handle);

            foreach (var drawable in drawables)
            {
                var i = drawable.ID;
                var maskIds = pointerToArray<int>(masks[i], maskCounts[i]);

                if (maskIds.Length == 0)
                    continue;

                var context = maskingContexts.FirstOrDefault(c => !c.Masks.Select(m => m.ID).Except(maskIds).Any());

                if (context == null)
                    maskingContexts.Add(context = new MaskingContext { Masks = drawables.Where(d => maskIds.Contains(d.ID)) });

                context.Drawables.Add(drawable);
                drawable.MaskingContext = context;
            }
        }

        private void initializeDrawableTextures()
        {
            // The texture store is only available in LoadComplete. However, we want all drawables to be loaded as soon as the constructor
            // is called. This is why we isolate this into its own method.
            var textureIds = CubismCore.csmGetDrawableTextureIndices(handle);
            foreach (var drawable in drawables)
                drawable.Texture = largeTextureStore?.Get(modelSetting?.FileReferences.Textures[textureIds[drawable.ID]] ?? string.Empty);
        }

        private void updateDrawables(CancellationTokenSource token)
        {
            var vertices = CubismCore.csmGetDrawableVertexPositions(handle);
            var opacities = CubismCore.csmGetDrawableOpacities(handle);
            var renderOrders = CubismCore.csmGetDrawableRenderOrders(handle);
            var dynamicFlags = CubismCore.csmGetDrawableDynamicFlags(handle);

            foreach (var drawable in drawables)
            {
                if (token.IsCancellationRequested)
                    break;

                int i = drawable.ID;
                var flags = dynamicFlags[i];

                bool hasUpdate = false;

                if (flags.HasFlagFast(CubismDynamicFlags.OpacityChanged))
                {
                    drawable.Alpha = opacities[i];
                    hasUpdate = true;
                }

                if (flags.HasFlagFast(CubismDynamicFlags.RenderOrderChanged))
                {
                    drawable.RenderOrder = renderOrders[i];
                    hasUpdate = true;
                }

                if (flags.HasFlagFast(CubismDynamicFlags.VertexPositionsChanged))
                {
                    drawable.Positions = pointerToArray<Vector2>(vertices[i], vertexCounts[i], token);
                    hasUpdate = true;
                }

                if (hasUpdate)
                    Schedule(() => renderer.Invalidate(Invalidation.DrawNode));
            }

            foreach (var context in maskingContexts)
            {
                if (token.IsCancellationRequested)
                    break;

                if (context.Bounds.IsEmpty)
                    continue;

                var inflatedBounds = context.Bounds.Inflate(0.05f);

                float scaleX = 1.0f / inflatedBounds.Width;
                float scaleY = 1.0f / inflatedBounds.Height;

                // These matrices are already transposed
                var matrixForMask = Matrix4.Identity;
                matrixForMask[0, 0] = 2.0f * scaleX;
                matrixForMask[1, 1] = 2.0f * scaleY;
                matrixForMask[3, 0] = -2.0f * scaleX * inflatedBounds.X - 1.0f;
                matrixForMask[3, 1] = -2.0f * scaleY * inflatedBounds.Y - 1.0f;

                context.MaskMatrix = matrixForMask;

                var matrixForDraw = Matrix4.Identity;
                matrixForDraw[0, 0] = scaleX;
                matrixForDraw[1, 1] = scaleY;
                matrixForDraw[3, 0] = -scaleX * inflatedBounds.X;
                matrixForDraw[3, 1] = -scaleY * inflatedBounds.Y;

                context.DrawMatrix = matrixForDraw;
            }
        }

        #endregion

        #region Parameter Management

        private readonly List<CubismParameter> parameters = new List<CubismParameter>();

        private float* parameterValues;

        private void initializeParameters()
        {
            parameterValues = CubismCore.csmGetParameterValues(handle);

            int count = CubismCore.csmGetParameterCount(handle);
            var def = CubismCore.csmGetParameterDefaultValues(handle);
            var min = CubismCore.csmGetParameterMinimumValues(handle);
            var max = CubismCore.csmGetParameterMaximumValues(handle);
            var names = CubismCore.csmGetParameterIds(handle);

            for (int i = 0; i < count; i++)
                parameters.Add(new CubismParameter(i, Marshal.PtrToStringAnsi((IntPtr)names[i]), min[i], max[i], def[i]));
        }

        private void copyParameterValuesFromModel()
        {
            for (int i = 0; i < parameters.Count; i++)
                parameters[i].Value = parameterValues[i];
        }

        private void copyParameterValuesToModel()
        {
            var values = parameters.Select(p => p.Value).ToArray();
            Marshal.Copy(values, 0, (IntPtr)parameterValues, values.Length);
        }

        #endregion

        #region Part Management

        private readonly List<CubismPart> parts = new List<CubismPart>();

        private float* partOpacityValues;

        private void initializeParts()
        {
            partOpacityValues = CubismCore.csmGetPartOpacities(handle);

            int count = CubismCore.csmGetPartCount(handle);
            var names = CubismCore.csmGetPartIds(handle);

            for (int i = 0; i < count; i++)
                parts.Add(new CubismPart(i, Marshal.PtrToStringAnsi((IntPtr)names[i])));
        }

        private void copyPartValuesFromModel()
        {
            var values = parts.Select(p => p.CurrentOpacity).ToArray();
            Marshal.Copy(values, 0, (IntPtr)partOpacityValues, values.Length);
        }

        private void copyPartValuesToModel()
        {
            for (int i = 0; i < parts.Count; i++)
                parts[i].CurrentOpacity = partOpacityValues[i];
        }

        #endregion

        protected override void LoadComplete()
        {
            base.LoadComplete();

            largeTextureStore = CreateTextureStore();

            initializeDrawableTextures();

            updateTaskCancellationToken = new CancellationTokenSource();
            updateTask = Task.Factory.StartNew(() => updateModelTask(updateTaskCancellationToken), updateTaskCancellationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void updateModelTask(CancellationTokenSource token)
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                    break;

                copyParameterValuesToModel();
                copyPartValuesToModel();

                CubismCore.csmUpdateModel(handle);

                copyParameterValuesFromModel();
                copyPartValuesFromModel();

                updateDrawables(token);

                CubismCore.csmResetDrawableDynamicFlags(handle);
            }
        }

        /// <summary>
        /// Create a texture store for this model. By default, it creates its own texture store but it must be overridden to share a single store.
        /// </summary>
        /// <returns>A texture store</returns>
        protected virtual LargeTextureStore CreateTextureStore() => resources != null ? new LargeTextureStore(host.CreateTextureLoaderStore(resources)) : null;

        //public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
        //{
        //    var local = ToLocalSpace(screenSpacePos);
        //    var rect = RectangleF.Empty;

        //    var hitAreas = modelSetting.HitAreas.Select(h => h.Id);

        //    if (!hitAreas.Any())
        //        return base.ReceivePositionalInputAt(screenSpacePos);

        //    foreach (var drawable in Drawables.Where(d => hitAreas.Contains(d.Name)))
        //        rect = RectangleF.Union(rect, drawable.VertexBounds);

        //    return rect.Contains(local);
        //}

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
                Marshal.FreeHGlobal(buffer);

            updateTaskCancellationToken.Cancel();
            updateTask.Wait();

            updateTask.Dispose();
            updateTask = null;

            updateTaskCancellationToken.Dispose();
            updateTaskCancellationToken = null;

            moc.Dispose();

            foreach (var context in maskingContexts)
                context.Dispose();

            base.Dispose(isDisposing);
        }

        private static T[] pointerToArray<T>(void* pointer, int length, CancellationTokenSource token = null)
        {
            var type = typeof(T);
            var output = new T[length];

            int size = Marshal.SizeOf<T>();

            if (type.IsPrimitive)
            {
                var handle = GCHandle.Alloc(output, GCHandleType.Pinned);
                var byteLength = length * size;

                var destination = (byte*)handle.AddrOfPinnedObject().ToPointer();

                for (int i = 0; i < byteLength; i++)
                    destination[i] = ((byte*)pointer)[i];

                handle.Free();
            }
            else if (type.IsValueType)
            {
                if (!type.IsLayoutSequential && !type.IsExplicitLayout)
                    throw new ArgumentException($"{type} does not define a StructLayout attribute.");

                for (int i = 0; i < length; i++)
                {
                    var offset = IntPtr.Add((IntPtr)pointer, i * size);

                    if (token?.IsCancellationRequested ?? false)
                        break;

                    output[i] = Marshal.PtrToStructure<T>(offset);
                }
            }
            else
            {
                throw new ArgumentException($"{type} is not a supported type.");
            }

            return output;
        }

        #region Controllers

        /// <summary>
        /// Adds a controller to this <see cref="CubismModel"/>.
        /// </summary>
        public void Add(CubismController controller) => AddInternal(controller);

        /// <summary>
        /// Adds a range of controllers to this <see cref="CubismModel"/>.
        /// </summary>
        public void AddRange(IEnumerable<CubismController> controllers) => AddRangeInternal(controllers);

        /// <summary>
        /// Removes a given controller in this <see cref="CubismModel"/>.
        /// </summary>
        /// <returns>False if the controller is not present on this <see cref="CubismModel"/>and true otherwise.</returns>
        public bool Remove(CubismController controller) => RemoveInternal(controller);

        /// <summary>
        /// The list of controllers this <see cref="CubismModel"/> has.
        /// Assigning to this property will dispose all existing controllers.
        /// </summary>
        public IEnumerable<CubismController> Children
        {
            get => InternalChildren.OfType<CubismController>();
            set
            {
                foreach (var controller in Children)
                    Remove(controller);

                AddRange(value);
            }
        }

        #endregion

        #region ICubismResourceProvider

        private LargeTextureStore largeTextureStore;
        private readonly CubismModelSetting modelSetting;
        private readonly IResourceStore<byte[]> resources;

        CubismModelSetting ICubismResourceProvider.Settings => modelSetting;

        LargeTextureStore ICubismResourceProvider.Textures => largeTextureStore;

        IResourceStore<byte[]> ICubismResourceProvider.Resources => resources;

        #endregion
    }
}