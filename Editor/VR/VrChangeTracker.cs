using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    // Poll native dirty versions; serialize/evaluate only after an actual edit.
    // Full validation still runs before VR starts and before every save.
    internal sealed class VrChangeTracker : IDisposable
    {
        struct Version
        {
            internal Object Target;
            internal int Dirty;
            internal Version(Object target) { Target = target; Dirty = EditorUtility.GetDirtyCount(target); }
            internal bool Changed => Target == null || EditorUtility.GetDirtyCount(Target) != Dirty;
        }

        readonly ComparisonSession session;
        readonly List<Version> sources = new List<Version>();
        readonly List<Version> candidates = new List<Version>();
        int revision = -1, sessionDirty, materialCount;
        bool sourceInvalidated, disposed;

        internal VrChangeTracker(ComparisonSession session)
        {
            this.session = session;
            CaptureSources();
            EditorApplication.hierarchyChanged += InvalidateSource;
            EditorApplication.projectChanged += InvalidateSource;
            Undo.undoRedoPerformed += InvalidateSource;
        }

        void InvalidateSource() => sourceInvalidated = true;

        internal void CaptureSources()
        {
            sources.Clear();
            var unique = new HashSet<Object>();
            if (session.Avatar != null)
                foreach (var component in session.Avatar.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    if (unique.Add(component.gameObject)) sources.Add(new Version(component.gameObject));
                    // Pose changes do not invalidate the comparison's configuration.
                    if (!(component is Transform) && !(component is Animator)
                        && component.GetType().FullName != "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"
                        && unique.Add(component)) sources.Add(new Version(component));
                }
            foreach (var slot in session.Slots)
                if (slot.Source != null && unique.Add(slot.Source)) sources.Add(new Version(slot.Source));
            sourceInvalidated = false;
        }

        internal bool SourceChanged()
        {
            if (sourceInvalidated || session.Avatar == null) return true;
            foreach (var root in session.Roots)
                if (root == null || !root.transform.IsChildOf(session.Avatar.transform)) return true;
            foreach (var renderer in session.Renderers)
                if (renderer == null || renderer.HasPropertyBlock()) return true;
            foreach (var version in sources) if (version.Changed) return true;
            return false;
        }

        internal bool CandidatesChanged()
        {
            if (revision != session.Revision || sessionDirty != EditorUtility.GetDirtyCount(session)) return true;
            var count = 0;
            foreach (var candidate in session.Candidates)
                foreach (var material in candidate.Materials)
                {
                    if (count >= candidates.Count || candidates[count].Target != material || candidates[count].Changed) return true;
                    count++;
                }
            return count != materialCount;
        }

        internal void CaptureCandidates()
        {
            candidates.Clear();
            foreach (var candidate in session.Candidates)
                foreach (var material in candidate.Materials)
                    candidates.Add(new Version(material));
            materialCount = candidates.Count; revision = session.Revision;
            sessionDirty = EditorUtility.GetDirtyCount(session);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            EditorApplication.hierarchyChanged -= InvalidateSource;
            EditorApplication.projectChanged -= InvalidateSource;
            Undo.undoRedoPerformed -= InvalidateSource;
        }
    }
}
