using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine.Lumin;
using UnityEngine.Scripting;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.MagicLeap.Internal;

namespace UnityEngine.XR.MagicLeap
{
    using MLLog = UnityEngine.XR.MagicLeap.MagicLeapLogger;

    [Preserve]
    [UsesLuminPrivilege("CameraCapture")]
    public sealed class MagicLeapImageTrackingSubsystem : XRImageTrackingSubsystem
    {
        const string kLogTag = "Unity-ImageTracking";

        [Conditional("DEVELOPMENT_BUILD")]
        static void DebugError(string msg) => LogError(msg);

        static void LogWarning(string msg) => MLLog.Warning(kLogTag, $"{msg}");
        static void LogError(string msg) => MLLog.Error(kLogTag, $"{msg}");
        static void Log(string msg) => MLLog.Debug(kLogTag, $"{msg}");

        internal static readonly string k_StreamingAssetsPath = Path.Combine(Application.streamingAssetsPath, "MLImageLibraries");

        internal static string GetDatabaseFilePathFromLibrary(XRReferenceImageLibrary library) => Path.Combine(k_StreamingAssetsPath, $"{library.name}_{library.guid}.imgpak");

        protected override Provider CreateProvider() => new MagicLeapProvider();

        /// <summary>
        /// Checks to see whether the native provider is valid and whether permission has been granted.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the native provider has been instantiated and has a valid native resource.
        /// </returns>
        /// <remarks>
        /// There are a number of reasons for false.  Either privileges were denied or the device experienced
        /// and internal error and was not able to create the native tracking resource.  Should the later be
        /// the case, native error logs will have more information.
        /// </remarks>
        public bool IsValid() => MagicLeapProvider.IsSubsystemStateValid();

        // Reference Counted Native Provider
        internal static IntPtr nativeProviderPtr => MagicLeapProvider.s_NativeProviderPtr;

        /// <summary>
        /// The <see cref="JobHandle"/> that refers to the native tracker handle creation job.
        /// </summary>
        /// <remarks>
        /// The creation of the native image tracker handle that enables image tracking on
        /// Lumin devices has an average startup time of anywhere between ~1500ms - ~6000ms
        /// depending on the state of the device and is blocking.  This subsystem opts to
        /// perform this operation asynchronously because of this.
        /// </remarks>
        public static JobHandle nativeTrackerCreationJobHandle => MagicLeapProvider.s_NativeTrackerCreationJobHandle;

        /// <summary>
        /// Returns whether the subsystem is in control of the stationary settings for images.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the subsystem is determining the stationary setting for images currently being tracked and <c>false</c> if the user is responsible.
        /// </returns>
        public bool GetAutomaticImageStationarySettingsEnforcementPolicy() => Native.GetAutomaticImageStationarySettingsEnforcementPolicy(nativeProviderPtr);

        /// <summary>
        /// Sets whether the subsystem should have ownership of the stationary setting for images based on the <c>"maxNumberOfMovingImages"</c>.
        /// </summary>
        /// <param name="shouldEnforceSubsystemOwnership">
        /// If passed in <c>true</c> the subsystem will automatically set the stationary parameter on images based on the <c>"maxNumberOfMovingImages"</c>.
        /// If passed in <c>false</c> the developer is responsible for settings the stationary parameter using <see cref="TrySetReferenceImageStationary"/>.
        /// </param>
        public void SetAutomaticImageStationarySettingsEnforcementPolicy(bool shouldEnforceSubsystemOwnership) => Native.SetAutomaticImageStationarySettingsEnforcementPolicy(nativeProviderPtr, shouldEnforceSubsystemOwnership);

        /// <summary>
        /// Attempts to set whether the subsystem should treat the given <c>XRReferenceImage</c> as stationary or not.
        /// </summary>
        /// <param name="referenceImage">The <c>XRReferenceImage</c> that should have its <c>isStationary</c> value changed.</param>
        /// <param name="isStationary">Whether or not the target image should be treated as a stationary image in the real world.  The default value is <c>false</c>.</param>
        /// <returns><c>true</c> if the image settings were updated successfully and <c>false</c> otherwise.</returns>
        /// <remarks>
        /// This will implicitly transfer ownership of the stationary setting for images from the subsystem to the user.  In order to give ownership back, use <see cref="SetAutomaticImageStationarySettingsEnforcementPolicy"/>.
        /// </remarks>
        public bool TrySetReferenceImageStationary(XRReferenceImage referenceImage, bool isStationary)
        {
            if (!IsValid())
                return false;

            return Native.TrySetReferenceImageStationary(nativeProviderPtr, referenceImage.textureGuid, isStationary);
        }

        class MagicLeapProvider : Provider
        {
            internal static IntPtr s_NativeProviderPtr = IntPtr.Zero;
            internal static JobHandle s_NativeTrackerCreationJobHandle = default(JobHandle);

            internal static bool IsSubsystemStateValid()
            {
                return s_NativeProviderPtr != IntPtr.Zero
                    && !s_NativeTrackerCreationJobHandle.Equals(default(JobHandle))
                    && s_NativeTrackerCreationJobHandle.IsCompleted
                    && Native.IsNativeTrackerHandleValid(s_NativeProviderPtr);
            }

            PerceptionHandle m_PerceptionHandle;

            /// <summary>
            /// The privilege required to access camera capture.
            /// </summary>
            const uint k_MLPrivilegeID_CameraCapture = 26;


            // This job is used to gate the creation of image libraries while also bypassing
            // the 6000ms wait time it takes to start a tracking system.  The job handle is then
            // passed with the handle to allow other asynchronous jobs to use it as a dependency.
            struct CreateNativeImageTrackerJob : IJob
            {
                [NativeDisableUnsafePtrRestriction]
                public IntPtr nativeProvider;

                public void Execute()
                {
                    if (!CreateNativeTracker(nativeProvider))
                    {
                        LogError($"Unable to create native tracker due to internal device error.  Subsystem will be set to invalid.  See native output for more details.");
                    }
                    RcoApi.Release(s_NativeProviderPtr);
                }

                [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_TryCreateNativeTracker")]
                public static extern bool CreateNativeTracker(IntPtr nativeProviderPtr);
            }

            /// <summary>
            /// Allows the user to re-request privileges
            /// </summary>
            /// <returns>
            /// <c>true</c> if the Color Camera privileges were granted and <c>false</c> otherwise.
            /// </returns>
            public bool RequestPrivilegesIfNecessary()
            {
                if (MagicLeapPrivileges.IsPrivilegeApproved(k_MLPrivilegeID_CameraCapture))
                {
                    return true;
                }
                else
                {
                    return MagicLeapPrivileges.RequestPrivilege(k_MLPrivilegeID_CameraCapture);
                }
            }

            public MagicLeapProvider()
            {
                // ARFoundation often beats XRManagement to the instantiation so often times the MagicLeapPrivileges
                // class will not be available to get privileges from.  Because of this the class is acquired here.
                MagicLeapPrivileges.Initialize();

                m_PerceptionHandle = PerceptionHandle.Acquire();


                if (s_NativeProviderPtr == IntPtr.Zero)
                    s_NativeProviderPtr = Native.Construct();
                if (RequestPrivilegesIfNecessary())
                {
                    if (s_NativeTrackerCreationJobHandle.Equals(default(JobHandle)))
                    {
                        RcoApi.Retain(s_NativeProviderPtr);
                        s_NativeTrackerCreationJobHandle = new CreateNativeImageTrackerJob { nativeProvider = s_NativeProviderPtr }.Schedule();
                    }
                }
                else
                {
                    LogWarning($"Could not start the image tracking subsystem because privileges were denied.");
                }
            }

            /// <summary>
            /// Destroy the image tracking subsystem.
            /// </summary>
            public override void Destroy()
            {
                if (s_NativeProviderPtr != IntPtr.Zero)
                {
                    Native.Destroy(s_NativeProviderPtr);
                    s_NativeProviderPtr = IntPtr.Zero;
                    s_NativeTrackerCreationJobHandle = default(JobHandle);
                }

                m_PerceptionHandle.Dispose();

                // Release retained privileges class
                MagicLeapPrivileges.Shutdown();
            }

            /// <summary>
            /// The current <c>RuntimeReferenceImageLibrary</c>.  If <c>null</c> then the subsystem will be set to "off".
            /// </summary>
            public override RuntimeReferenceImageLibrary imageLibrary
            {
                set
                {
                    if (RequestPrivilegesIfNecessary())
                    {
                        if (value == null)
                        {
                            Native.SetDatabase(s_NativeProviderPtr, IntPtr.Zero);
                        }
                        else if (value is MagicLeapImageDatabase database)
                        {
                            if (database.nativeProviderPtr != s_NativeProviderPtr)
                            {
                                throw new InvalidOperationException($"Attempted to set an invalid image library.  The native resource for this library has been released making the library invalid.");
                            }

                            Native.SetDatabase(s_NativeProviderPtr, database.nativePtr);
                        }
                        else
                        {
                            throw new ArgumentException($"The {value.GetType().Name} is not a valid Magic Leap image library.");
                        }
                    }
                }
            }

            public unsafe override TrackableChanges<XRTrackedImage> GetChanges(XRTrackedImage defaultTrackedImage, Allocator allocator)
            {
                if (!IsSubsystemStateValid())
                    return default(TrackableChanges<XRTrackedImage>);

                void* addedPtr, updatedPtr, removedPtr;
                int addedLength, updatedLength, removedLength, stride;

                var context = Native.AcquireChanges(
                    s_NativeProviderPtr,
                    out addedPtr, out addedLength,
                    out updatedPtr, out updatedLength,
                    out removedPtr, out removedLength,
                    out stride);

                try
                {
                    return new TrackableChanges<XRTrackedImage>(
                        addedPtr, addedLength,
                        updatedPtr, updatedLength,
                        removedPtr, removedLength,
                        defaultTrackedImage, stride,
                        allocator);
                }
                finally
                {
                    Native.ReleaseChanges(context);
                }
            }

            /// <summary>
            /// Sets the maximum number of moving targets.
            /// </summary>
            public override int maxNumberOfMovingImages
            {
                set => Native.TrySetAllActiveReferenceImagesToStationary(s_NativeProviderPtr, value);
            }

            /// <summary>
            /// Creates a <c>RuntimeReferenceImageLibrary</c> from the passed in <c>XRReferenceImageLibrary</c> passed in.
            /// </summary>
            /// <param name="serializedLibrary">The <c>XRReferenceImageLibrary</c> that is used to create the <c>RuntimeReferenceImageLibrary</c></param>
            /// <returns>A new <c>RuntimeReferenceImageLibrary</c> created from the old  </returns>
            public override RuntimeReferenceImageLibrary CreateRuntimeLibrary(XRReferenceImageLibrary serializedLibrary)
            {
                if (s_NativeProviderPtr == IntPtr.Zero || s_NativeTrackerCreationJobHandle.Equals(default(JobHandle)))
                    return null;

                return new MagicLeapImageDatabase(serializedLibrary, s_NativeProviderPtr, s_NativeTrackerCreationJobHandle);
            }
        }

        static internal class Native
        {
            public static readonly ulong InvalidHandle = ulong.MaxValue;

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_Construct")]
            public static extern IntPtr Construct();

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_Destroy")]
            public static extern void Destroy(IntPtr nativeProviderPtr);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_IsNativeTrackerHandleValid")]
            public static extern bool IsNativeTrackerHandleValid(IntPtr nativeProviderPtr);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_SetDatabase")]
            public static extern void SetDatabase(IntPtr nativeProviderPtr, IntPtr database);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_AcquireChanges")]
            public static extern unsafe void* AcquireChanges(
                IntPtr nativeProviderPtr,
                out void* addedPtr, out int addedLength,
                out void* updatedPtr, out int updatedLength,
                out void* removedPtr, out int removedLength,
                out int stride);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_ReleaseChanges")]
            public static extern unsafe void ReleaseChanges(void* changes);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_TrySetMaximumNumberOfMovingImages")]
            public static extern bool TrySetAllActiveReferenceImagesToStationary(IntPtr nativeProviderPtr, int count);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_GetAutomaticStationaryImageSettingsEnforcementPolicy")]
            public static extern bool GetAutomaticImageStationarySettingsEnforcementPolicy(IntPtr nativeProviderPtr);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_SetAutomaticStationaryImageSettingsEnforcementPolicy")]
            public static extern void SetAutomaticImageStationarySettingsEnforcementPolicy(IntPtr nativeProviderPtr, bool shouldEnforceSubsystemOwnership);

            [DllImport("UnityMagicLeap", CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnityMagicLeap_ImageTracking_TrySetReferenceImageStationary")]
            public static extern bool TrySetReferenceImageStationary(IntPtr nativeProviderPtr, Guid guid, bool isStationary);
        }

#if UNITY_2019_2_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        static void RegisterDescriptor()
        {
#if PLATFORM_LUMIN
            XRImageTrackingSubsystemDescriptor.Create(new XRImageTrackingSubsystemDescriptor.Cinfo
            {
                id = "MagicLeap-ImageTracking",
                subsystemImplementationType = typeof(MagicLeapImageTrackingSubsystem),
                supportsMovingImages = true,
                supportsMutableLibrary = true
            });
#else
            // Do something but don't initialize if we are not using PLATFORM_LUMIN
            var subsystemDescriptorInfo = new XRImageTrackingSubsystemDescriptor.Cinfo
            {
                id = "MagicLeap-ImageTracking",
                subsystemImplementationType = typeof(MagicLeapImageTrackingSubsystem),
                supportsMovingImages = true,
                supportsMutableLibrary = true
            };
#endif
        }
    }
}
