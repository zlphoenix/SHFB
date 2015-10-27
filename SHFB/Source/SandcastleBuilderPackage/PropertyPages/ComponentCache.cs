﻿//===============================================================================================================
// System  : Sandcastle Help File Builder Visual Studio Package
// File    : ComponentCache.cs
// Author  : Eric Woodruff
// Updated : 10/26/2015
// Note    : Copyright 2015, Eric Woodruff, All rights reserved
// Compiler: Microsoft Visual C#
//
// This is used to create shared instances of a composition container used to access help file builder
// components within the project property pages.
//
// This code is published under the Microsoft Public License (Ms-PL).  A copy of the license should be
// distributed with the code and can be found at the project website: https://GitHub.com/EWSoftware/SHFB.  This
// notice, the author's name, and all copyright notices must remain intact in all applications, documentation,
// and source files.
//
//    Date     Who  Comments
// ==============================================================================================================
// 10/22/2015  EFW  Created the code
//===============================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition.Hosting;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Sandcastle.Core;

namespace SandcastleBuilder.Package.PropertyPages
{
    /// <summary>
    /// This is used to create shared instances of a composition container used to access help file builder
    /// components.
    /// </summary>
    /// <remarks>The composition container is created asynchronously and is shared amongst the property pages to
    /// provide better responsiveness in the UI.</remarks>
    public sealed class ComponentCache
    {
        #region Private data members
        //=====================================================================

        private static ConcurrentDictionary<string, ComponentCache> instances = new ConcurrentDictionary<string,ComponentCache>();

        private string projectName;
        private object syncRoot;
        private HashSet<string> lastSearchFolders;
        private CompositionContainer componentContainer;
        private CancellationTokenSource cancellationTokenSource;

        #endregion

        #region Properties
        //=====================================================================

        /// <summary>
        /// This read-only property returns the shared component container
        /// </summary>
        public CompositionContainer ComponentContainer
        {
            get
            {
                CompositionContainer container;

                lock(syncRoot)
                {
                    container = componentContainer;
                }

                return container;
            }
        }

        /// <summary>
        /// This read-only property returns the last error that occurred, if any
        /// </summary>
        /// <value>This will be set if the <see cref="ComponentContainerLoadFailed"/> event is raised</value>
        public Exception LastError { get; private set; }

        #endregion

        #region Events
        //=====================================================================

        /// <summary>
        /// This event is raised to notify pages that the shared component container has been loaded
        /// </summary>
        public event EventHandler ComponentContainerLoaded;

        /// <summary>
        /// This raises the <see cref="ComponentContainerLoaded"/> event
        /// </summary>
        private void OnComponentContainerLoaded()
        {
            var handler = ComponentContainerLoaded;

            if(handler != null)
                handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// This event is raised to notify pages that the shared component container has been reset
        /// </summary>
        public event EventHandler ComponentContainerReset;

        /// <summary>
        /// This raises the <see cref="ComponentContainerReset"/> event
        /// </summary>
        private void OnComponentContainerReset()
        {
            var handler = ComponentContainerReset;

            if(handler != null)
                handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// This event is raised to notify pages that the shared component container failed to get loaded
        /// </summary>
        public event EventHandler ComponentContainerLoadFailed;

        /// <summary>
        /// This raises the <see cref="ComponentContainerLoadFailed"/> event
        /// </summary>
        private void OnComponentContainerLoadFailed()
        {
            var handler = ComponentContainerLoadFailed;

            if(handler != null)
                handler(this, EventArgs.Empty);
        }
        #endregion

        #region Private constructor
        //=====================================================================

        /// <summary>
        /// Private constructor
        /// </summary>
        /// <param name="projectName">The project name with which the component cache is associated</param>
        private ComponentCache(string projectName)
        {
            this.projectName = projectName;
            syncRoot = new Object();
            lastSearchFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        #endregion

        #region Methods
        //=====================================================================

        /// <summary>
        /// Create a component cache for the given project name
        /// </summary>
        /// <param name="projectName"></param>
        /// <returns>A new component cache if one does not already exist or the current instance if one does
        /// already exist.</returns>
        internal static ComponentCache CreateComponentCache(string projectName)
        {
            ComponentCache cache;

            if(!instances.TryGetValue(projectName, out cache))
                cache = instances.AddOrUpdate(projectName, new ComponentCache(projectName), (k, v) => v);

            return cache;
        }

        /// <summary>
        /// Clear the component cache
        /// </summary>
        internal static void Clear()
        {
            foreach(string key in instances.Keys)
                RemoveComponentCache(key);
        }

        /// <summary>
        /// Remove a component cache
        /// </summary>
        internal static void RemoveComponentCache(string projectName)
        {
            ComponentCache cache;

            if(instances.TryRemove(projectName, out cache))
                lock(cache.syncRoot)
                {
                    if(cache.cancellationTokenSource != null)
                        cache.cancellationTokenSource.Cancel();

                    if(cache.componentContainer != null)
                        cache.componentContainer.Dispose();
                }
        }

        /// <summary>
        /// Load the component container with everything found in the given set of folders
        /// </summary>
        /// <param name="searchFolders">The folders to search</param>
        /// <returns>True if it has already been loaded, false if it has not.  If not, it will be loaded
        /// asynchronously.  The <see cref="ComponentContainerReset"/> and <see cref="ComponentContainerLoaded"/>
        /// events will be raised accordingly.</returns>
        public bool LoadComponentContainer(IEnumerable<string> searchFolders)
        {
            lock(syncRoot)
            {
                if(cancellationTokenSource == null)
                {
                    if(componentContainer != null)
                    {
                        if(searchFolders.Count() == lastSearchFolders.Count &&
                          searchFolders.All(f => lastSearchFolders.Contains(f)))
                            return true;
                    }

                    lastSearchFolders.Clear();
                    lastSearchFolders.UnionWith(searchFolders);

                    if(componentContainer != null)
                    {
                        componentContainer.Dispose();
                        componentContainer = null;
                    }

                    this.OnComponentContainerReset();
                    this.CreateComponentContainerInternal(searchFolders);
                }

                return false;
            }
        }

        /// <summary>
        /// This handles the creation of the component container asynchronously
        /// </summary>
        /// <param name="searchFolders">The folders to search for component assemblies</param>
        private async void CreateComponentContainerInternal(IEnumerable<string> searchFolders)
        {
            try
            {
                this.LastError = null;

                cancellationTokenSource = new CancellationTokenSource();

                var result = await Task.Run(() => ComponentUtilities.CreateComponentContainer(searchFolders,
                    cancellationTokenSource.Token), cancellationTokenSource.Token);

                lock(syncRoot)
                {
                    componentContainer = result;
                }
            }
            catch(OperationCanceledException)
            {
                // Ignore this one
            }
            catch(Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.ToString());

                // Could have passed this in a derived EventArgs instance but I couldn't be bothered to create
                // one just for this since the probability of an exception is extremely low here.
                this.LastError = ex;
                
                this.OnComponentContainerLoadFailed();
            }
            finally
            {
                lock(syncRoot)
                {
                    if(cancellationTokenSource != null)
                    {
                        if(!cancellationTokenSource.IsCancellationRequested && this.LastError == null)
                            this.OnComponentContainerLoaded();

                        cancellationTokenSource.Dispose();
                        cancellationTokenSource = null;
                    }
                }
            }
        }
        #endregion
    }
}
