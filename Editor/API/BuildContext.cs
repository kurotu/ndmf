﻿#region

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf.localization;
using nadena.dev.ndmf.reporting;
using nadena.dev.ndmf.runtime;
using nadena.dev.ndmf.ui;
using nadena.dev.ndmf.util;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;
using UnityObject = UnityEngine.Object;

#endregion

namespace nadena.dev.ndmf
{
    internal sealed class ExecutionScope : IDisposable
    {
        private readonly ErrorReportScope _errorReportScope;
        private readonly ObjectRegistryScope _objectRegistryScope;

        public ExecutionScope(BuildContext ctx)
        {
            _errorReportScope = new ErrorReportScope(ctx._report);
            _objectRegistryScope = new ObjectRegistryScope(ctx._registry);
        }

        public void Dispose()
        {
            _errorReportScope.Dispose();
            _objectRegistryScope.Dispose();
        }
    }

    /// <summary>
    /// The BuildContext is passed to all plugins during the build process. It provides access to the avatar being
    /// built, as well as various other context information.
    /// </summary>
    public sealed partial class BuildContext
    {
        private readonly GameObject _avatarRootObject;
        private readonly Transform _avatarRootTransform;

        private Stopwatch sw = new Stopwatch();
        internal readonly ObjectRegistry _registry;
        internal readonly ErrorReport _report;

        public ObjectRegistry ObjectRegistry => _registry;
        public ErrorReport ErrorReport => _report;

        /// <summary>
        /// The root GameObject of the avatar being built.
        /// </summary>
        public GameObject AvatarRootObject => _avatarRootObject;

        /// <summary>
        /// The root Transform of the avatar being built.
        /// </summary>
        public Transform AvatarRootTransform => _avatarRootTransform;

        /// <summary>
        /// An asset container that can be used to store generated assets. NDMF will automatically add any objects
        /// referenced by the avatar to this container when the build completes, but in some cases it can be necessary
        /// to manually save assets (e.g. when using AnimatorController builtins).
        /// </summary>
        public UnityObject AssetContainer { get; private set; }

        public bool Successful => !_report.Errors.Any(e => e.TheError.Severity >= ErrorSeverity.Error);

        private Dictionary<Type, object> _state = new Dictionary<Type, object>();
        private Dictionary<Type, IExtensionContext> _extensions = new Dictionary<Type, IExtensionContext>();
        private Dictionary<Type, IExtensionContext> _activeExtensions = new Dictionary<Type, IExtensionContext>();

        public T GetState<T>() where T : new()
        {
            if (_state.TryGetValue(typeof(T), out var value))
            {
                return (T)value;
            }

            value = new T();
            _state[typeof(T)] = value;
            return (T)value;
        }

        public T Extension<T>() where T : IExtensionContext
        {
            if (!_activeExtensions.TryGetValue(typeof(T), out var value))
            {
                throw new Exception($"Extension {typeof(T)} not active");
            }

            return (T)value;
        }

        public BuildContext(GameObject obj, string assetRootPath, bool isClone = true)
        {
            BuildEvent.Dispatch(new BuildEvent.BuildStarted(obj));
            _registry = new ObjectRegistry(obj.transform);
            _report = ErrorReport.Create(obj, isClone);

            Debug.Log("Starting processing for avatar: " + obj.name);
            sw.Start();

            _avatarRootObject = obj;
            _avatarRootTransform = obj.transform;

#if NDMF_VRCSDK3_AVATARS
            PlatformInit();
#endif

            var avatarName = _avatarRootObject.name;

            AssetContainer = ScriptableObject.CreateInstance<GeneratedAssets>();
            if (assetRootPath != null)
            {
                // Ensure the target directory exists
                Directory.CreateDirectory(assetRootPath);

                var pathAvatarName = FilterAvatarName(avatarName);

                var avatarPath = Path.Combine(assetRootPath, pathAvatarName) + ".asset";
                AssetDatabase.GenerateUniqueAssetPath(avatarPath);
                AssetDatabase.CreateAsset(AssetContainer, avatarPath);
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(AssetContainer)))
                {
                    throw new Exception("Failed to persist asset container");
                }
            }

            // Ensure that no prefab instances remain somehow
            foreach (Transform t in _avatarRootTransform.GetComponentsInChildren<Transform>(true))
            {
                if (PrefabUtility.IsPartOfAnyPrefab(t))
                {
                    throw new Exception("Can't process an avatar that contains prefab instances/assets");
                }
            }

            sw.Stop();

            // Register all initially-existing GameObjects and Components
            using (new ObjectRegistryScope(_registry))
            {
                foreach (Transform xform in _avatarRootTransform.GetComponentsInChildren<Transform>(true))
                {
                    ObjectRegistry.GetReference(xform.gameObject);

                    foreach (Component c in xform.gameObject.GetComponents<Component>())
                    {
                        ObjectRegistry.GetReference(c);
                    }
                }
            }
        }

        private static readonly Regex WindowsReservedFileNames = new Regex(
            "(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])([.].*)?",
            RegexOptions.IgnoreCase
        );

        private static readonly Regex WindowsReservedFileCharacters = new Regex(
            "[<>:\"/\\\\|?*\x00-\x1f]",
            RegexOptions.IgnoreCase
        );

        private static readonly Regex StripLeadingTrailingWhitespace = new Regex(
            "^[\\s]*((?=\\S).*\\S)[\\s]*$"
        );

        internal static string FilterAvatarName(string avatarName)
        {
            avatarName = WindowsReservedFileCharacters.Replace(avatarName, "_");

            if (WindowsReservedFileNames.IsMatch(avatarName))
            {
                avatarName = "_" + avatarName;
            }

            var match = StripLeadingTrailingWhitespace.Match(avatarName);
            if (match.Success)
            {
                avatarName = match.Groups[1].Value;
            } else {
                avatarName = Guid.NewGuid().ToString();
            }

            return avatarName;
        }

        public bool IsTemporaryAsset(UnityObject obj)
        {
            return !EditorUtility.IsPersistent(obj)
                   || AssetDatabase.GetAssetPath(obj) == AssetDatabase.GetAssetPath(AssetContainer);
        }

        public void Serialize()
        {
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(AssetContainer)))
            {
                return; // unit tests with no serialized assets
            }

            HashSet<UnityObject> _savedObjects =
                new HashSet<UnityObject>(AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(AssetContainer)));

            _savedObjects.Remove(AssetContainer);

            int index = 0;
            foreach (var asset in _avatarRootObject.ReferencedAssets(traverseSaved: true, includeScene: false))
            {
                if (asset is MonoScript)
                {
                    // MonoScripts aren't considered to be a Main or Sub-asset, but they can't be added to asset
                    // containers either.
                    continue;
                }

                if (_savedObjects.Contains(asset))
                {
                    _savedObjects.Remove(asset);
                    continue;
                }

                if (asset == null)
                {
                    Debug.Log($"Asset {index} is null");
                }

                index++;

                if (!EditorUtility.IsPersistent(asset))
                {
                    try
                    {
                        AssetDatabase.AddObjectToAsset(asset, AssetContainer);
                    }
                    catch (UnityException ex)
                    {
                        Debug.Log(
                            $"Error adding asset {asset} p={AssetDatabase.GetAssetOrScenePath(asset)} isMain={AssetDatabase.IsMainAsset(asset)} " +
                            $"isSub={AssetDatabase.IsSubAsset(asset)} isForeign={AssetDatabase.IsForeignAsset(asset)} isNative={AssetDatabase.IsNativeAsset(asset)}");
                        throw ex;
                    }
                }
            }

            // Remove obsolete temporary assets
            foreach (var asset in _savedObjects)
            {
                if (!(asset is Component || asset is GameObject))
                {
                    // Traversal can't currently handle prefabs, so this must have been manually added. Avoid purging it.
                    continue;
                }

                AssetDatabase.RemoveObjectFromAsset(asset);
            }

            // SaveAssets to make sub-assets visible on the Project window
            AssetDatabase.SaveAssets();
        }

        public void DeactivateExtensionContext<T>() where T : IExtensionContext
        {
            DeactivateExtensionContext(typeof(T));
        }

        public void DeactivateExtensionContext(Type t)
        {
            using (new ExecutionScope(this))
            using (_report.WithExtensionContextTrace(t))
                try
                {
                    if (_activeExtensions.ContainsKey(t))
                    {
                        var ctx = _activeExtensions[t];
                        Profiler.BeginSample("NDMF Deactivate: " + t);
                        try
                        {
                            ctx.OnDeactivate(this);
                        }
                        finally
                        {
                            Profiler.EndSample();
                        }

                        _activeExtensions.Remove(t);
                    }
                }
                catch (Exception e)
                {
                    ErrorReport.ReportException(e);
                }
        }

        internal void RunPass(ConcretePass pass)
        {
            using (new ExecutionScope(this))
            using (_report.WithContext(pass.Plugin as PluginBase))
            using (_report.WithContextPassName(pass.Description))
            {
                sw.Start();

                ImmutableDictionary<Type, double> deactivationTimes = ImmutableDictionary<Type, double>.Empty;

                foreach (var ty in pass.DeactivatePlugins)
                {
                    Stopwatch sw2 = new Stopwatch();
                    sw2.Start();
                    DeactivateExtensionContext(ty);
                    deactivationTimes = deactivationTimes.Add(ty, sw2.Elapsed.TotalMilliseconds);
                }

                ImmutableDictionary<Type, double> activationTimes = ImmutableDictionary<Type, double>.Empty;
                foreach (var ty in pass.ActivatePlugins)
                {
                    Stopwatch sw2 = new Stopwatch();
                    sw2.Start();
                    ActivateExtensionContext(ty);
                    activationTimes = activationTimes.Add(ty, sw2.Elapsed.TotalMilliseconds);
                }

                Stopwatch passTimer = new Stopwatch();
                passTimer.Start();
                Profiler.BeginSample(pass.Description);
                try
                {
                    pass.Execute(this);
                }
                catch (Exception e)
                {
                    pass.Plugin.OnUnhandledException(e);
                    ErrorReport.ReportException(e);
                }
                finally
                {
                    Profiler.EndSample();
                    passTimer.Stop();
                }

                BuildEvent.Dispatch(new BuildEvent.PassExecuted(
                    pass.InstantiatedPass.QualifiedName,
                    passTimer.Elapsed.TotalMilliseconds,
                    activationTimes,
                    deactivationTimes
                ));

                sw.Stop();
            }
        }

        public T ActivateExtensionContext<T>() where T : IExtensionContext
        {
            return (T)ActivateExtensionContext(typeof(T));
        }

        public IExtensionContext ActivateExtensionContext(Type ty)
        {
            using (new ExecutionScope(this))
            using (_report.WithExtensionContextTrace(ty))
                try
                {
                    if (!_extensions.TryGetValue(ty, out var ctx))
                    {
                        ctx = (IExtensionContext)ty.GetConstructor(Type.EmptyTypes).Invoke(Array.Empty<object>());
                    }

                    if (!_activeExtensions.ContainsKey(ty))
                    {
                        Profiler.BeginSample("NDMF Activate: " + ty);
                        try
                        {
                            ctx.OnActivate(this);
                        }
                        finally
                        {
                            Profiler.EndSample();
                        }

                        _activeExtensions.Add(ty, ctx);
                    }

                    return _activeExtensions[ty];
                }
                catch (Exception e)
                {
                    ErrorReport.ReportException(e);
                    return null;
                }
        }

        internal void Finish()
        {
            using (new ExecutionScope(this))
            {
                sw.Start();
                foreach (var kvp in _activeExtensions.ToList())
                {
                    using (_report.WithExtensionContextTrace(kvp.Key))
                    {
                        try
                        {
                            kvp.Value.OnDeactivate(this);

                            // ReSharper disable once SuspiciousTypeConversion.Global
                            if (kvp.Value is IDisposable d)
                            {
                                d.Dispose();
                            }
                        }
                        catch (Exception e)
                        {
                            ErrorReport.ReportException(e);
                        }
                    }

                    _activeExtensions.Remove(kvp.Key);
                }

                Serialize();
                sw.Stop();

                BuildEvent.Dispatch(new BuildEvent.BuildEnded(sw.ElapsedMilliseconds, true));

                if (!Application.isBatchMode && _report.Errors.Count > 0)
                {
                    ErrorReportWindow.ShowReport(_report);
                }
            }
        }
    }
}