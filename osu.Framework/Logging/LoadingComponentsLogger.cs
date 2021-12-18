// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Development;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Framework.Lists;

namespace osu.Framework.Logging
{
    internal static class LoadingComponentsLogger
    {
        private static readonly WeakList<Drawable> loading_components = new WeakList<Drawable>();

        public static void Add(Drawable component)
        {
            if (!DebugUtils.IsDebugBuild) return;

            lock (loading_components)
                loading_components.Add(component);
        }

        public static void Remove(Drawable component)
        {
            if (!DebugUtils.IsDebugBuild) return;

            lock (loading_components)
                loading_components.Remove(component);
        }

        public static void LogAndFlush()
        {
            if (!DebugUtils.IsDebugBuild) return;

            lock (loading_components)
            {
                Logger.Log("⏳ Currently loading components");

                foreach (var c in loading_components)
                    Logger.Log($"- {c.GetType().ReadableName(),-16} LoadState:{c.LoadState,-5} Thread:{c.LoadThread.Name}");

                loading_components.Clear();
            }
        }
    }
}