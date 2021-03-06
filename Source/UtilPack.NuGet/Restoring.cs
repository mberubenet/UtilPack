﻿/*
 * Copyright 2017 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Repositories;
using NuGet.Versioning;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UtilPack;
using UtilPack.NuGet;
using NuGet.RuntimeModel;
using NuGet.Packaging;
using NuGet.Protocol;

#if !NUGET_430
using TLocalNuspecCache = NuGet.Protocol.
#if NUGET_440 || NUGET_450
         LocalNuspecCache
#else
LocalPackageFileCache
#endif
         ;
#endif


namespace UtilPack.NuGet
{
   /// <summary>
   /// This is event argument class for <see cref="BoundRestoreCommandUser.PackageSpecCreated"/> event.
   /// </summary>
   public sealed class PackageSpecCreatedArgs
   {
      /// <summary>
      /// Creates a new instance of <see cref="PackageSpecCreatedArgs"/> with given <see cref="global::NuGet.ProjectModel.PackageSpec"/>.
      /// </summary>
      /// <param name="packageSpec">The <see cref="global::NuGet.ProjectModel.PackageSpec"/> that was created.</param>
      /// <exception cref="ArgumentNullException">If <paramref name="packageSpec"/> is <c>null</c>.</exception>
      public PackageSpecCreatedArgs( PackageSpec packageSpec )
      {
         this.PackageSpec = ArgumentValidator.ValidateNotNull( nameof( packageSpec ), packageSpec );
      }

      /// <summary>
      /// Gets the <see cref="global::NuGet.ProjectModel.PackageSpec"/> that was created.
      /// </summary>
      /// <value>The <see cref="global::NuGet.ProjectModel.PackageSpec"/> that was created.</value>
      public PackageSpec PackageSpec { get; }
   }

   /// <summary>
   /// This class binds itself to specific <see cref="NuGetFramework"/> and then performs restore commands via <see cref="RestoreIfNeeded"/> method.
   /// It also caches results so that restore command is invoked only if needed.
   /// </summary>
   public class BoundRestoreCommandUser : IDisposable
   {
      public const String DEFAULT_RUNTIME_GRAPH_PACKAGE_ID = "Microsoft.NETCore.Platforms";

      private readonly SourceCacheContext _cacheContext;
      private readonly RestoreCommandProviders _restoreCommandProvider;
      private readonly String _nugetRestoreRootDir; // NuGet restore command never writes anything to disk (apart from packages themselves), but if certain file paths are omitted, it simply fails with argumentnullexception when invoking Path.Combine or Path.GetFullName. So this can be anything, really, as long as it's understandable by Path class.
      private readonly TargetFrameworkInformation _restoreTargetFW;
      private LockFile _previousLockFile;
      private readonly ConcurrentDictionary<String, ConcurrentDictionary<NuGetVersion, RestoreResult>> _allLockFiles;
      private readonly Boolean _disposeSourceCacheContext;

      /// <summary>
      /// Creates new instance of <see cref="BoundRestoreCommandUser"/> with given parameters.
      /// </summary>
      /// <param name="nugetSettings">The settings to use.</param>
      /// <param name="thisFramework">The framework to bind to.</param>
      /// <param name="runtimeIdentifier">The runtime identifier. Will be used by <see cref="E_UtilPack.ExtractAssemblyPaths{TResult}(BoundRestoreCommandUser, LockFile, Func{string, IEnumerable{string}, TResult}, GetFileItemsDelegate, IEnumerable{string})"/> method.</param>
      /// <param name="runtimeGraph">Optional value indicating runtime graph information: either <see cref="global::NuGet.RuntimeModel.RuntimeGraph"/> directly, or <see cref="String"/> containing package ID of package holding <c>runtime.json</c> file, containing serialized runtime graph definition. If neither is specified, then <c>"Microsoft.NETCore.Platforms"</c> package ID used to locate <c>runtime.json</c> file, as per <see href="https://docs.microsoft.com/en-us/dotnet/core/rid-catalog">official documentation</see>.</param>
      /// <param name="nugetLogger">The logger to use in restore command.</param>
      /// <param name="sourceCacheContext">The optional <see cref="SourceCacheContext"/> to use.</param>
      /// <param name="nuspecCache">The optional <see cref="TLocalNuspecCache"/> to use.</param>
      /// <param name="leaveSourceCacheOpen">Whether to leave the <paramref name="sourceCacheContext"/> open when disposing this <see cref="BoundRestoreCommandUser"/>.</param>
      /// <exception cref="ArgumentNullException">If <paramref name="nugetSettings"/> is <c>null</c>.</exception>
      public BoundRestoreCommandUser(
         ISettings nugetSettings,
         NuGetFramework thisFramework = null,
         String runtimeIdentifier = null,
         EitherOr<RuntimeGraph, String> runtimeGraph = default,
         ILogger nugetLogger = null,
         SourceCacheContext sourceCacheContext = null,
#if !NUGET_430
         TLocalNuspecCache nuspecCache = null,
#endif
         Boolean leaveSourceCacheOpen = false
         )
      {
         ArgumentValidator.ValidateNotNull( nameof( nugetSettings ), nugetSettings );
         this.ThisFramework = thisFramework ?? UtilPackNuGetUtility.TryAutoDetectThisProcessFramework();
         if ( nugetLogger == null )
         {
            nugetLogger = NullLogger.Instance;
         }

         var global = SettingsUtility.GetGlobalPackagesFolder( nugetSettings );
         var fallbacks = SettingsUtility.GetFallbackPackageFolders( nugetSettings );
         if ( sourceCacheContext == null )
         {
            leaveSourceCacheOpen = false;
         }
         var ctx = sourceCacheContext ?? new SourceCacheContext();
         var psp = new PackageSourceProvider( nugetSettings );
         var csp = new CachingSourceProvider( psp );
         this.RuntimeIdentifier = UtilPackNuGetUtility.TryAutoDetectThisProcessRuntimeIdentifier( runtimeIdentifier );
         this._cacheContext = ctx;
         this._disposeSourceCacheContext = !leaveSourceCacheOpen;

         this.NuGetLogger = nugetLogger;
         this._restoreCommandProvider = RestoreCommandProviders.Create(
            global,
            fallbacks,
            new PackageSourceProvider( nugetSettings ).LoadPackageSources().Where( s => s.IsEnabled ).Select( s => csp.CreateRepository( s ) ),
            ctx,
#if !NUGET_430
            nuspecCache ?? new TLocalNuspecCache(),
#endif
            nugetLogger
            );
         this._nugetRestoreRootDir = Path.Combine( Path.GetTempPath(), Path.GetRandomFileName() );
         this._restoreTargetFW = new TargetFrameworkInformation()
         {
            FrameworkName = this.ThisFramework
         };
         this.LocalRepositories = this._restoreCommandProvider.GlobalPackages.Singleton().Concat( this._restoreCommandProvider.FallbackPackageFolders ).ToDictionary( r => r.RepositoryRoot, r => r );
         this.RuntimeGraph = new Lazy<RuntimeGraph>( () =>
            {
               var rGraph = runtimeGraph.GetFirstOrDefault();
               if ( rGraph == null )
               {
                  var packageName = runtimeGraph.GetSecondOrDefault();
                  if ( String.IsNullOrEmpty( packageName ) )
                  {
                     packageName = DEFAULT_RUNTIME_GRAPH_PACKAGE_ID;
                  }
                  var platformsPackagePath = this.LocalRepositories.Values
                     .SelectMany( r => r.FindPackagesById( packageName ) )
                     .OrderByDescending( p => p.Version )
                     .FirstOrDefault()
                     ?.ExpandedPath;
                  rGraph = String.IsNullOrEmpty( platformsPackagePath ) ?
                  null :
                  global::NuGet.RuntimeModel.JsonRuntimeFormat.ReadRuntimeGraph( Path.Combine( platformsPackagePath, global::NuGet.RuntimeModel.RuntimeGraph.RuntimeGraphFileName ) );
               }
               return rGraph;
            }, LazyThreadSafetyMode.ExecutionAndPublication );

         this._allLockFiles = new ConcurrentDictionary<String, ConcurrentDictionary<NuGetVersion, RestoreResult>>();
      }

      /// <summary>
      /// Gets the framework that this <see cref="BoundRestoreCommandUser"/> is bound to.
      /// </summary>
      /// <value>The framework that this <see cref="BoundRestoreCommandUser"/> is bound to.</value>
      public NuGetFramework ThisFramework { get; }

      /// <summary>
      /// Gets the logger used in restore command.
      /// </summary>
      /// <value>The framework used in restore command.</value>
      public ILogger NuGetLogger { get; }

      /// <summary>
      /// Gets the local repositories by their root path.
      /// </summary>
      /// <value>The local repositories by their root path.</value>
      public IReadOnlyDictionary<String, NuGetv3LocalRepository> LocalRepositories { get; }

      /// <summary>
      /// Gets the runtime identifier that this <see cref="BoundRestoreCommandUser"/> is bound to.
      /// </summary>
      /// <value>The runtime identifier that this <see cref="BoundRestoreCommandUser"/> is bound to.</value>
      public String RuntimeIdentifier { get; }

      /// <summary>
      /// Gets the <see cref="Lazy{T}"/> holding the <see cref="global::NuGet.RuntimeModel.RuntimeGraph"/> of this <see cref="BoundRestoreCommandUser"/>.
      /// </summary>
      /// <value>The <see cref="Lazy{T}"/> holding the <see cref="global::NuGet.RuntimeModel.RuntimeGraph"/> of this <see cref="BoundRestoreCommandUser"/>.</value>
      public Lazy<RuntimeGraph> RuntimeGraph { get; }

      /// <summary>
      /// This event can be registered to in order to further modify the <see cref="PackageSpec"/> that was created during restore.
      /// </summary>
      public event GenericEventHandler<PackageSpecCreatedArgs> PackageSpecCreated;

      /// <summary>
      /// Performs restore command for given combinations of package and version.
      /// Will use cached results, if available.
      /// Returns resulting lock file.
      /// </summary>
      /// <param name="token">The cancellation token, in case actual restore will need to be performed.</param>
      /// <param name="packageInfo">The package ID + version combination. The package version should be parseable into <see cref="VersionRange"/>. If the version is <c>null</c> or empty, <see cref="VersionRange.AllFloating"/> will be used.</param>
      /// <returns>Cached or restored lock file.</returns>
      /// <seealso cref="RestoreCommand"/>
      public async Task<LockFile> RestoreIfNeeded(
         CancellationToken token,
         params (String PackageID, String PackageVersion)[] packageInfo
         )
      {

         LockFile retVal;
         if ( !packageInfo.IsNullOrEmpty() )
         {
            // Prepare for invoking restore command
            var versionRanges = packageInfo
               .Select( tuple => String.IsNullOrEmpty( tuple.PackageVersion ) ? VersionRange.AllFloating : VersionRange.Parse( tuple.PackageVersion ) )
               .ToArray();
            var cachedResults = versionRanges.Select( ( versionRange, idx ) =>
            {
               RestoreResult curLockFile = null;
               if ( !versionRange.IsFloating && this._allLockFiles.TryGetValue( packageInfo[idx].PackageID, out var thisPackageLockFiles ) )
               {
                  var matchingActualVersion = versionRange.FindBestMatch( thisPackageLockFiles.Keys.Where( v => versionRange.Satisfies( v ) ) );
                  if ( matchingActualVersion != null )
                  {
                     curLockFile = thisPackageLockFiles[matchingActualVersion];
                  }
               }

               return curLockFile;
            } )
            .Where( l => l != null )
            .ToArray();

            if ( cachedResults.Length == packageInfo.Length )
            {
               // All lock files are cached
               // Need to merge into single LockFile
               var remoteWalkContext = new global::NuGet.DependencyResolver.RemoteWalkContext( this._cacheContext, this.NuGetLogger );
               foreach ( var local in this._restoreCommandProvider.LocalProviders )
               {
                  remoteWalkContext.LocalLibraryProviders.Add( local );
               }
               foreach ( var remote in this._restoreCommandProvider.RemoteProviders )
               {
                  remoteWalkContext.RemoteLibraryProviders.Add( remote );
               }


               retVal = new LockFileBuilder(
                  cachedResults[0].LockFile.Version,
                  this.NuGetLogger,
                  new Dictionary<RestoreTargetGraph, Dictionary<String, LibraryIncludeFlags>>()
                  ).CreateLockFile(
                     cachedResults[0].LockFile, // Previous lock file
                     this.CreatePackageSpec( versionRanges.Select( ( v, idx ) => (packageInfo[idx].PackageID, v) ).ToArray() ), // PackageSpec
                     cachedResults.SelectMany( r => r.RestoreGraphs ).ToArray(), // Restore Graphs
                     this.LocalRepositories.Values.ToList(), // Local repos
                     remoteWalkContext // Remote walk context
                     );
            }
            else
            {
               // Need to invoke restore command
               var result = await this.PerformRestore( versionRanges.Select( ( v, idx ) => (packageInfo[idx].PackageID, v) ).ToArray(), token );
               retVal = result.LockFile;
               foreach ( var tuple in packageInfo )
               {
                  var packageID = tuple.PackageID;
                  var actualVersion = retVal.Libraries.FirstOrDefault( l => String.Equals( l.Name, packageID ) )?.Version;
                  if ( actualVersion != null )
                  {
                     this._allLockFiles
                        .GetOrAdd( packageID, p => new ConcurrentDictionary<NuGetVersion, RestoreResult>() )
                        .TryAdd( actualVersion, result );
                  }
               }
               Interlocked.Exchange( ref this._previousLockFile, retVal );
            }
         }
         else
         {
            retVal = null;
         }

         return retVal;
      }

      /// <summary>
      /// This method is invoked by <see cref="RestoreIfNeeded"/> when the lock file is not in cache and restore command needs to be actually run.
      /// </summary>
      /// <param name="targets">What packages to restore.</param>
      /// <param name="token">The cancellation token to use when performing restore.</param>
      /// <returns>The <see cref="LockFile"/> generated by <see cref="RestoreCommand"/>.</returns>
      protected virtual async Task<RestoreResult> PerformRestore(
         (String ID, VersionRange Version)[] targets,
         CancellationToken token
         )
      {
         var request = new RestoreRequest(
            this.CreatePackageSpec( targets ),
            this._restoreCommandProvider,
            this._cacheContext,
            this.NuGetLogger
            )
         {
            ProjectStyle = ProjectStyle.Standalone,
            RestoreOutputPath = this._nugetRestoreRootDir,
            ExistingLockFile = this._previousLockFile
         };
         return await ( new RestoreCommand( request ).ExecuteAsync( token ) );
      }

      /// <summary>
      /// Helper method to create <see cref="PackageSpec"/> out of package ID + version combinations.
      /// </summary>
      /// <param name="targets">The package ID + version combinations.</param>
      /// <returns>A new instance of <see cref="PackageSpec"/> having <see cref="PackageSpec.TargetFrameworks"/> and <see cref="PackageSpec.Dependencies"/> populated as needed.</returns>
      protected PackageSpec CreatePackageSpec( (String ID, VersionRange Version)[] targets )
      {
         var projectName = $"Restoring: {String.Join( ", ", targets )}";
         var spec = new PackageSpec()
         {
            Name = projectName,
            FilePath = Path.Combine( this._nugetRestoreRootDir, "dummy" ),
            RestoreMetadata = new ProjectRestoreMetadata() // restore command will call GetBuildIntegratedProjectCacheFilePath, which will use request.Project.RestoreMetadata.ProjectPath without null-checks
            {
               ProjectPath = "dummy.csproj",
               ProjectName = projectName // If this is left to anything else than project name, Nuget.Commands.TransitiveNoWarnUtils.ExtractTransitiveNoWarnProperties method will fail to nullref or key-not-found
            },
            // TODO now that this class has its own RuntimeGraph, investigate whether we can use it here. On preliminar check, it seems though that only equality comparison, hash coding, and serializing uses this property.
            //RuntimeGraph = new RuntimeGraph( new RuntimeDescription( this.RuntimeIdentifier ).Singleton() )
         };

         this.PackageSpecCreated?.Invoke( new PackageSpecCreatedArgs( spec ) );

         spec.TargetFrameworks.Add( this._restoreTargetFW );

         foreach ( var tuple in targets )
         {
            if ( !String.IsNullOrEmpty( tuple.ID ) )
            {
               spec.Dependencies.Add( new LibraryDependency()
               {
                  LibraryRange = new LibraryRange( tuple.ID, tuple.Version, LibraryDependencyTarget.Package )
               } );
            }
         }
         return spec;
      }

      /// <summary>
      /// Disposes the managed resources held by this <see cref="BoundRestoreCommandUser"/>
      /// </summary>
      public void Dispose()
      {
         if ( this._disposeSourceCacheContext )
         {
            this._cacheContext.DisposeSafely();
         }
      }
   }

   /// <summary>
   /// This delegate is used by <see cref="E_UtilPack.ExtractAssemblyPaths"/> in order to get required assembly paths from single <see cref="LockFileTargetLibrary"/>.
   /// </summary>
   /// <param name="runtimeGraph">The lazy holding <see cref="RuntimeGraph"/> containing information about various runtime identifiers (RIDs) and their dependencies.</param>
   /// <param name="currentRID">The current RID string.</param>
   /// <param name="targetLibrary">The current <see cref="LockFileTargetLibrary"/>.</param>
   /// <param name="libraries">Lazily evaluated dictionary of all <see cref="LockFileLibrary"/> instances, based on package ID.</param>
   /// <returns>The assembly paths for this <see cref="LockFileTargetLibrary"/>.</returns>
   public delegate IEnumerable<String> GetFileItemsDelegate( Lazy<RuntimeGraph> runtimeGraph, String currentRID, LockFileTargetLibrary targetLibrary, Lazy<IDictionary<String, LockFileLibrary>> libraries );

   public static partial class UtilPackNuGetUtility
   {

      /// <summary>
      /// This is constant for generic Windows runtime - without version or architecture information.
      /// </summary>
      public const String RID_WINDOWS = "win";

      /// <summary>
      /// This is constant for generic Unix runtime - without version or architecture information.
      /// </summary>
      public const String RID_UNIX = "unix";

      /// <summary>
      /// This is constant for generic Linux runtime - without version or architecture information.
      /// </summary>
      public const String RID_LINUX = "linux";

      /// <summary>
      /// This is constant for generic OSX runtime - without version or architecture information.
      /// </summary>
      public const String RID_OSX = "osx";

      /// <summary>
      /// Gets the default <see cref="GetFileItemsDelegate"/> which will return all runtime assemblies for given <see cref="LockFileTargetLibrary"/>.
      /// </summary>
      public static GetFileItemsDelegate GetRuntimeAssembliesDelegate { get; } = ( runtimeGraph, currentRID, targetLib, libs ) =>
      {
         IEnumerable<String> retVal = null;
         if ( !String.IsNullOrEmpty( currentRID ) && targetLib.RuntimeTargets.Count > 0 )
         {
            var rGraph = runtimeGraph.Value;
            retVal = targetLib.RuntimeTargets
                  .Where( rt => rGraph.AreCompatible( currentRID, rt.Runtime ) )
                  .Select( rt => rt.Path );
         }
         if ( retVal.IsNullOrEmpty() )
         {
            retVal = targetLib.RuntimeAssemblies.Select( ra => ra.Path );
         }
         else
         {
            // Filter out the ones which have same name as native ones
            var arr = retVal.Select( p => Path.GetFileNameWithoutExtension( p ) ).ToArray();
            retVal = retVal.Concat( targetLib.RuntimeAssemblies.Where( ra => Array.IndexOf( arr, Path.GetFileNameWithoutExtension( ra.Path ) ) < 0 ).Select( ra => ra.Path ) );
         }
         return retVal;
      };

      /// <summary>
      /// Helper method to compute closed set of dependencies of given package IDs, using information of this <see cref="LockFileTarget"/>.
      /// </summary>
      /// <param name="target">This <see cref="LockFileTarget"/>.</param>
      /// <param name="packageIDs">The IDs of entrypoint packages.</param>
      /// <param name="additionalCheck">Optional additional check for single <see cref="PackageDependency"/>. If supplied, all dependencies that the callback returns <c>false</c> will be filtered out.</param>
      /// <returns>An enumerable of all direct and indirect dependencies of given package IDs, including the package IDs themselves.</returns>
      public static IEnumerable<LockFileTargetLibrary> GetAllDependencies(
         this LockFileTarget target,
         IEnumerable<String> packageIDs,
         Func<PackageDependency, Boolean> additionalCheck = null
         )
      {
         var targetLibsDictionary = target.Libraries.ToDictionary( lib => lib.Name, lib => lib );
         IEnumerable<LockFileTargetLibrary> GetChildrenExceptFilterable( LockFileTargetLibrary curLib )
         {
            return curLib.Dependencies
                   .Where( dep => !String.IsNullOrEmpty( dep.Id ) && targetLibsDictionary.ContainsKey( dep.Id ) && ( additionalCheck?.Invoke( dep ) ?? true ) )
                   .Select( dep => targetLibsDictionary[dep.Id] );
         }

         //filterablePackagesArray = filterablePackagesArray
         //   .Where( f => targetLibsDictionary.ContainsKey( f ) )
         //   .Select( f => targetLibsDictionary[f] )
         //   .SelectMany( targetLib => targetLib.AsDepthFirstEnumerableWithLoopDetection( curLib => GetChildrenExceptFilterable( curLib, false ), returnHead: true ) )
         //   .Select( targetLib => targetLib.Name )
         //   .Distinct()
         //   .ToArray();

         return packageIDs
            .Where( pID => targetLibsDictionary.ContainsKey( pID ) )
            .Select( pID => targetLibsDictionary[pID] )
            .SelectMany( targetLib => targetLib.AsDepthFirstEnumerableWithLoopDetection( curLib => GetChildrenExceptFilterable( curLib ), returnHead: true ) )
            .Distinct();
      }


      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some Windows runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_WINDOWS"/> case insensitively.</returns>
      public static Boolean IsWindowsRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_WINDOWS );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some Unix runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_UNIX"/> case insensitively.</returns>
      public static Boolean IsUnixRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_UNIX );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some Linux runtime (generic, but not specific).
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_LINUX"/> case insensitively.</returns>
      public static Boolean IsGenericLinuxRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_LINUX );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some OSX runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_OSX"/> case insensitively.</returns>
      public static Boolean IsOSXRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_OSX );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some other generic runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <param name="genericRID">The generic RID to test against.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <paramref name="genericRID"/> case insensitively.</returns>
      public static Boolean IsOfGenericRID( this String thisRID, String genericRID )
      {
         return !String.IsNullOrEmpty( thisRID ) && !String.IsNullOrEmpty( genericRID ) && thisRID.StartsWith( genericRID, StringComparison.OrdinalIgnoreCase );
      }
   }
}

/// <summary>
/// Contains extension methods for types defined in this assembly
/// </summary>
public static partial class E_UtilPack
{
   /// <summary>
   /// Performs restore command for given package and version, if not already cached.
   /// Returns resulting lock file.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="packageID">The package ID to use.</param>
   /// <param name="version">The version to use. Should be parseable into <see cref="VersionRange"/>. If <c>null</c> or empty, <see cref="VersionRange.AllFloating"/> will be used.</param>
   /// <param name="token">The optional cancellation token.</param>
   /// <returns>Cached or restored lock file.</returns>
   /// <seealso cref="RestoreCommand"/>
   public static Task<LockFile> RestoreIfNeeded(
      this BoundRestoreCommandUser restorer,
      String packageID,
      String version,
      CancellationToken token = default( CancellationToken )
      )
   {
      return restorer.RestoreIfNeeded( token, (packageID, version) );
   }

   /// <summary>
   /// Performs restore command for given package, if not already cached.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="packageInfo">The package information containing package ID and package version.</param>
   /// <returns>Cached or restored lock file.</returns>
   /// <seealso cref="RestoreCommand"/>
   public static Task<LockFile> RestoreIfNeeded(
      this BoundRestoreCommandUser restorer,
      params (String PackageID, String version)[] packageInfo
      )
   {
      return restorer.RestoreIfNeeded( default( CancellationToken ), packageInfo );
   }

   /// <summary>
   /// This is helper method to extract assembly path information from <see cref="LockFile"/> potentially produced by <see cref="BoundRestoreCommandUser.RestoreIfNeeded"/>.
   /// The key will be package ID, and the result will be object generated by <paramref name="resultCreator"/>.
   /// </summary>
   /// <typeparam name="TResult">The type containing information about assembly paths for a single package.</typeparam>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="lockFile">The <see cref="LockFile"/> containing information about packages.</param>
   /// <param name="resultCreator">The callback to create <typeparamref name="TResult"/> from assemblies of a single package.</param>
   /// <param name="fileGetter">Optional callback to extract assembly paths from single <see cref="LockFileTargetLibrary"/>.</param>
   /// <param name="filterablePackages">Optional array of package IDs which will be (along with their dependencies) filtered out from result.</param>
   /// <returns>A dictionary containing assembly paths of packages.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="BoundRestoreCommandUser"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="lockFile"/> or <paramref name="resultCreator"/> is <c>null</c>.</exception>
   public static IDictionary<String, TResult> ExtractAssemblyPaths<TResult>(
      this BoundRestoreCommandUser restorer,
      LockFile lockFile,
      Func<String, IEnumerable<String>, TResult> resultCreator,
      GetFileItemsDelegate fileGetter = null,
      IEnumerable<String> filterablePackages = null
   )
   {
      ArgumentValidator.ValidateNotNullReference( restorer );
      ArgumentValidator.ValidateNotNull( nameof( lockFile ), lockFile );
      ArgumentValidator.ValidateNotNull( nameof( resultCreator ), resultCreator );

      var retVal = new Dictionary<String, TResult>();
      if ( fileGetter == null )
      {
         fileGetter = UtilPackNuGetUtility.GetRuntimeAssembliesDelegate;
      }
      var libDic = new Lazy<IDictionary<String, LockFileLibrary>>( () =>
      {
         return lockFile.Libraries.ToDictionary( lib => lib.Name, lib => lib );
      }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication );

      // We will always have only one target, since we are running restore always against one target framework
      IEnumerable<LockFileTargetLibrary> libraries = lockFile.Targets[0].Libraries;
      ISet<String> filterablePackagesSet;
      if (
         filterablePackages != null
         && ( filterablePackagesSet = new HashSet<String>( filterablePackages ) ).Count > 0
         )
      {
         libraries = lockFile.Targets[0].GetAllDependencies(
            lockFile.PackageSpec.Dependencies.Select( dep => dep.Name ),
            dep => !filterablePackagesSet.Contains( dep.Id )
            );
      }

      foreach ( var targetLib in libraries )
      {
         var curLib = targetLib;
         var targetLibFullPath = restorer.ResolveFullPath( lockFile, pathResolver => pathResolver.GetPackageDirectory( curLib.Name, curLib.Version ) );
         if ( !String.IsNullOrEmpty( targetLibFullPath ) )
         {
            retVal.Add( curLib.Name, resultCreator(
               targetLibFullPath,
               fileGetter( restorer.RuntimeGraph, restorer.RuntimeIdentifier, curLib, libDic )
                  ?.Select( p => Path.Combine( targetLibFullPath, p ) )
                  ?? Empty<String>.Enumerable
               ) );
         }
      }

      return retVal;
   }

   /// <summary>
   /// Helper method to resolve full path from relative path (to one of the <see cref="LockFile.PackageFolders"/>) with the lock file.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="lockFile">The <see cref="LockFile"/>.</param>
   /// <param name="pathWithinPackageFolder">Some path originating from <paramref name="lockFile"/> and relative to <see cref="LockFile.PackageFolders"/>.</param>
   /// <returns>The full path, or <c>null</c>.</returns>
   public static String ResolveFullPath( this BoundRestoreCommandUser restorer, LockFile lockFile, String pathWithinPackageFolder )
   {
      return restorer.ResolveFullPath(
         lockFile,
         String.IsNullOrEmpty( pathWithinPackageFolder ) ?
            (Func<VersionFolderPathResolver, String>) null :
            _ => pathWithinPackageFolder
         );
   }

   /// <summary>
   /// Helper method to resolve full path from relative path (to one of the <see cref="LockFile.PackageFolders"/>) callback.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="lockFile">The <see cref="LockFile"/>.</param>
   /// <param name="pathExtractor">The callback which should return path relative to <see cref="LockFile.PackageFolders"/>.</param>
   /// <returns>The full path, or <c>null</c>.</returns>
   public static String ResolveFullPath( this BoundRestoreCommandUser restorer, LockFile lockFile, Func<VersionFolderPathResolver, String> pathExtractor )
   {
      ArgumentValidator.ValidateNotNullReference( restorer );
      var onlyOnePackageFolder = ArgumentValidator.ValidateNotNull( nameof( lockFile ), lockFile ).PackageFolders.Count == 1;
      return pathExtractor == null ? null : lockFile.PackageFolders
         .Select( f =>
         {
            return restorer.LocalRepositories.TryGetValue( f.Path, out var curRepo ) ?
                  Path.Combine( curRepo.RepositoryRoot, pathExtractor( curRepo.PathResolver ) ) :
                  null;
         } )
         .FirstOrDefault( fp => !String.IsNullOrEmpty( fp ) && ( onlyOnePackageFolder || Directory.Exists( fp ) ) );
   }


}