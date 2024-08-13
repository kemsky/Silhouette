﻿using ProfilerLib;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.ComponentModel;

namespace ManagedDotnetProfiler;

internal unsafe partial class CorProfiler : CorProfilerCallback10Base
{
    private readonly ConcurrentDictionary<AssemblyId, bool> _assemblyLoads = new();
    private readonly ConcurrentDictionary<ClassId, bool> _classLoads = new();
    private readonly ConcurrentDictionary<int, int> _nestedCatchBlocks = new();
    private readonly ConcurrentDictionary<int, int> _nestedExceptionSearchFilter = new();
    private readonly ConcurrentDictionary<int, int> _nestedExceptionSearchFunction = new();
    private readonly ConcurrentDictionary<int, int> _nestedExceptionUnwindFinally = new();
    private readonly ConcurrentDictionary<int, int> _nestedExceptionUnwindFunction = new();
    private int _garbageCollectionsInProgress;

    public static CorProfiler Instance { get; private set; }

    public static ConcurrentQueue<string> Logs { get; } = new();

    public bool GetThreadId(ulong expectedThreadId, int expectedOsId)
    {
        var (result, threadId) = ICorProfilerInfo.GetCurrentThreadId();

        if (!result.IsOK)
        {
            LogHResult(nameof(ICorProfilerInfo.GetCurrentThreadId), result);
            return false;
        }

        Log($"GetThreadId - expected: {expectedThreadId} - actual: {threadId.Value}");

        if (expectedThreadId != threadId.Value)
        {
            return false;
        }

        // Can't call GetThreadInfo in the CLR thread
        int osId = 0;

        Task.Run(() =>
        {
            (result, osId) = ICorProfilerInfo.GetThreadInfo(threadId);
        }).Wait();

        if (!result.IsOK)
        {
            LogHResult(nameof(ICorProfilerInfo.GetThreadInfo), result);
            return false;
        }

        Log($"GetThreadInfo - expected: {expectedOsId} - actual: {osId}");

        return true;
    }

    protected override HResult Initialize(int iCorProfilerInfoVersion)
    {
        if (iCorProfilerInfoVersion < 11)
        {
            return HResult.E_FAIL;
        }

        Instance = this;

        var eventMask = CorPrfMonitor.COR_PRF_MONITOR_ALL;
        var highEventMask = CorPrfHighMonitor.COR_PRF_HIGH_MONITOR_DYNAMIC_FUNCTION_UNLOADS;

        Log($"Setting event mask to {eventMask}");
        Log($"Setting high event mask to {highEventMask}");

        return ICorProfilerInfo11.SetEventMask2(eventMask, CorPrfHighMonitor.COR_PRF_HIGH_MONITOR_DYNAMIC_FUNCTION_UNLOADS);
    }

    protected override HResult JITCompilationStarted(FunctionId functionId, bool fIsSafeToBlock)
    {
        Log($"JITCompilationStarted - {GetFunctionFullName(functionId)}");
        return HResult.S_OK;
    }

    protected override HResult JITCompilationFinished(FunctionId functionId, HResult hrStatus, bool fIsSafeToBlock)
    {
        Log($"JITCompilationFinished - {GetFunctionFullName(functionId)}");
        return HResult.S_OK;
    }

    protected override HResult JITFunctionPitched(FunctionId functionId)
    {
        Environment.FailFast("Never called by the CLR");
        return HResult.E_NOTIMPL;
    }

    protected override HResult ManagedToUnmanagedTransition(FunctionId functionId, COR_PRF_TRANSITION_REASON reason)
    {
        var functionName = GetFunctionFullName(functionId);

        // Don't log FetchLastLog to avoid producing new logs while fetching logs
        if (!functionName.Contains("FetchLastLog"))
        {
            Log($"ManagedToUnmanagedTransition - {functionName} - {reason}");
        }

        return HResult.S_OK;
    }

    protected override HResult UnmanagedToManagedTransition(FunctionId functionId, COR_PRF_TRANSITION_REASON reason)
    {
        var functionName = GetFunctionFullName(functionId);

        // Don't log FetchLastLog to avoid producing new logs while fetching logs
        if (!functionName.Contains("FetchLastLog"))
        {
            Log($"UnmanagedToManagedTransition - {functionName} - {reason}");
        }

        return HResult.S_OK;
    }

    protected override HResult JITInlining(FunctionId callerId, FunctionId calleeId, out bool pfShouldInline)
    {
        Log($"JITInlining - {GetFunctionFullName(calleeId)} -> {GetFunctionFullName(callerId)}");
        pfShouldInline = true;
        return HResult.S_OK;
    }

    protected override HResult RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON suspendReason)
    {
        Log($"RuntimeSuspendStarted - {suspendReason}");
        return HResult.S_OK;
    }

    protected override HResult RuntimeSuspendFinished()
    {
        Log("RuntimeSuspendFinished");
        return HResult.S_OK;
    }

    protected override HResult RuntimeResumeStarted()
    {
        Log("RuntimeResumeStarted");
        return HResult.S_OK;
    }

    protected override HResult RuntimeResumeFinished()
    {
        Log("RuntimeResumeFinished");
        return HResult.S_OK;
    }

    protected override HResult RuntimeThreadSuspended(ThreadId threadId)
    {
        var (_, osId) = ICorProfilerInfo.GetThreadInfo(threadId);
        Logs.Enqueue($"RuntimeThreadSuspended - {osId}");
        return HResult.S_OK;
    }

    protected override HResult RuntimeThreadResumed(ThreadId threadId)
    {
        var (_, osId) = ICorProfilerInfo.GetThreadInfo(threadId);
        Logs.Enqueue($"RuntimeThreadResumed - {osId}");
        return HResult.S_OK;
    }

    protected override HResult ThreadCreated(ThreadId threadId)
    {
        var (_, osId) = ICorProfilerInfo.GetThreadInfo(threadId);
        Logs.Enqueue($"ThreadCreated - {osId}");
        return HResult.S_OK;
    }

    protected override HResult ThreadDestroyed(ThreadId threadId)
    {
        var (_, osId) = ICorProfilerInfo.GetThreadInfo(threadId);
        Logs.Enqueue($"ThreadDestroyed - {osId}");
        return HResult.S_OK;
    }

    protected override HResult ThreadAssignedToOSThread(ThreadId managedThreadId, int osThreadId)
    {
        Logs.Enqueue($"ThreadAssignedToOSThread - {osThreadId}");
        return HResult.S_OK;
    }

    protected override HResult ThreadNameChanged(ThreadId threadId, uint cchName, char* name)
    {
        var threadName = new Span<char>(name, (int)cchName);
        Logs.Enqueue($"ThreadNameChanged - {threadName}");
        return HResult.S_OK;
    }

    protected override HResult ExceptionSearchCatcherFound(FunctionId functionId)
    {
        var (_, _, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId);
        var (_, metaDataImport) = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport);
        var (_, methodProperties) = metaDataImport.GetMethodProps(new MdMethodDef(mdToken));
        var (_, typeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class);

        Log($"ExceptionSearchCatcherFound - {typeName}.{methodProperties.Name}");
        return HResult.S_OK;
    }

    protected override HResult AppDomainCreationStarted(AppDomainId appDomainId)
    {
        var (_, appDomainName, processId) = ICorProfilerInfo.GetAppDomainInfo(appDomainId);

        Log($"AppDomainCreationStarted - {appDomainName} - Process Id {processId.Value}");

        return HResult.S_OK;
    }

    protected override HResult AppDomainCreationFinished(AppDomainId appDomainId, HResult hrStatus)
    {
        var (_, appDomainName, _) = ICorProfilerInfo.GetAppDomainInfo(appDomainId);

        Log($"AppDomainCreationFinished - {appDomainName} - HResult {hrStatus}");

        return HResult.S_OK;
    }

    protected override HResult AppDomainShutdownStarted(AppDomainId appDomainId)
    {
        // TODO: Test on .NET Framework
        var (_, appDomainName, _) = ICorProfilerInfo.GetAppDomainInfo(appDomainId);

        Log($"AppDomainShutdownStarted - {appDomainName}");

        return HResult.S_OK;
    }

    protected override HResult AppDomainShutdownFinished(AppDomainId appDomainId, HResult hrStatus)
    {
        // TODO: Test on .NET Framework
        var (_, appDomainName, _) = ICorProfilerInfo.GetAppDomainInfo(appDomainId);

        Log($"AppDomainShutdownFinished - {appDomainName} - HResult {hrStatus}");

        return HResult.S_OK;
    }

    protected override HResult AssemblyLoadStarted(AssemblyId assemblyId)
    {
        if (!_assemblyLoads.TryAdd(assemblyId, true))
        {
            Error($"Assembly {assemblyId} already loading");
        }

        return HResult.S_OK;
    }

    protected override HResult AssemblyLoadFinished(AssemblyId assemblyId, HResult hrStatus)
    {
        var (_, assemblyName, appDomainId, moduleId) = ICorProfilerInfo.GetAssemblyInfo(assemblyId);
        var (_, appDomainName, _) = ICorProfilerInfo.GetAppDomainInfo(appDomainId);
        var (_, moduleName, _, _) = ICorProfilerInfo.GetModuleInfo(moduleId);

        Log($"AssemblyLoadFinished - {assemblyName} - AppDomain {appDomainName} - Module {moduleName}");

        if (!_assemblyLoads.TryRemove(assemblyId, out _))
        {
            Error($"Saw no AssemblyLoadStarted event for {assemblyId.Value}");
        }

        return HResult.S_OK;
    }

    protected override HResult AssemblyUnloadStarted(AssemblyId assemblyId)
    {
        // TODO: Test on .NET Framework or after the ALC bug is fixed
        Log("AssemblyUnloadStarted");

        return HResult.S_OK;
    }

    protected override HResult AssemblyUnloadFinished(AssemblyId assemblyId, HResult hrStatus)
    {
        var (_, assemblyName, appDomainId, moduleId) = ICorProfilerInfo.GetAssemblyInfo(assemblyId);
        var (_, appDomainName, _) = ICorProfilerInfo.GetAppDomainInfo(appDomainId);
        var (_, moduleName, _, _) = ICorProfilerInfo.GetModuleInfo(moduleId);

        Log($"AssemblyUnloadFinished - {assemblyName} - AppDomain {appDomainName} - Module {moduleName}");

        return HResult.S_OK;
    }

    protected override HResult ClassLoadStarted(ClassId classId)
    {
        if (!_classLoads.TryAdd(classId, true))
        {
            Error($"Class {classId.Value} already loading");
        }

        return HResult.S_OK;
    }

    protected override HResult ClassLoadFinished(ClassId classId, HResult hrStatus)
    {
        Log($"ClassLoadFinished - {GetTypeNameFromClassId(classId)}");

        if (!_classLoads.TryRemove(classId, out _))
        {
            Error($"Saw no ClassLoadStarted event for {classId.Value}");
        }

        return HResult.S_OK;
    }

    protected override HResult ClassUnloadStarted(ClassId classId)
    {
        Log($"ClassUnloadStarted - {GetTypeNameFromClassId(classId)}");
        return HResult.S_OK;
    }

    protected override HResult ClassUnloadFinished(ClassId classId, HResult hrStatus)
    {
        Log($"ClassUnloadFinished - {GetTypeNameFromClassId(classId)}");
        return HResult.S_OK;
    }

    protected override unsafe HResult COMClassicVTableCreated(ClassId wrappedClassId, in Guid implementedIID, void* pVTable, uint cSlots)
    {
        Log($"COMClassicVTableCreated - {GetTypeNameFromClassId(wrappedClassId)} - {implementedIID} - {cSlots}");
        return HResult.S_OK;
    }

    protected override unsafe HResult COMClassicVTableDestroyed(ClassId wrappedClassId, in Guid implementedIID, void* pVTable)
    {
        Log("Error: the profiling API never raises the event COMClassicVTableDestroyed");
        return HResult.S_OK;
    }

    protected override unsafe HResult ConditionalWeakTableElementReferences(uint cRootRefs, ObjectId* keyRefIds, ObjectId* valueRefIds, GCHandleId* rootIds)
    {
        var (_, stringLengthOffset, bufferOffset) = ICorProfilerInfo2.GetStringLayout().ThrowIfFailed();

        var stringPtr1 = (byte*)(*keyRefIds).Value;
        var str1 = new ReadOnlySpan<char>(stringPtr1 + bufferOffset, Unsafe.Read<int>(stringPtr1 + stringLengthOffset));

        var stringPtr2 = (byte*)(*valueRefIds).Value;
        var str2 = new ReadOnlySpan<char>(stringPtr2 + bufferOffset, Unsafe.Read<int>(stringPtr2 + stringLengthOffset));

        Log($"ConditionalWeakTableElementReferences - {str1} -> {str2}");

        return HResult.S_OK;
    }

    protected override unsafe HResult DynamicMethodJITCompilationStarted(FunctionId functionId, bool fIsSafeToBlock, byte* pILHeader, uint cbILHeader)
    {
        Log($"DynamicMethodJITCompilationStarted - {functionId.Value:x2}");
        return HResult.S_OK;
    }

    protected override HResult DynamicMethodJITCompilationFinished(FunctionId functionId, HResult hrStatus, bool fIsSafeToBlock)
    {
        Log($"DynamicMethodJITCompilationFinished - {functionId.Value:x2}");
        return HResult.S_OK;
    }

    protected override HResult DynamicMethodUnloaded(FunctionId functionId)
    {
        Log($"DynamicMethodUnloaded - {functionId.Value:x2}");
        return HResult.S_OK;
    }

    protected override HResult ExceptionCatcherEnter(FunctionId functionId, ObjectId objectId)
    {
        var typeName = GetTypeNameFromObjectId(objectId);

        var (_, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId).ThrowIfFailed();
        var metaDataImport = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport).ThrowIfFailed();
        var methodProperties = metaDataImport.GetMethodProps(new MdMethodDef(mdToken)).ThrowIfFailed();
        var (functionTypeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class).ThrowIfFailed();

        Log($"ExceptionCatcherEnter - catch {typeName} in {functionTypeName}.{methodProperties.Name}");

        _nestedCatchBlocks.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, old) => old + 1);

        // It's weird but ExceptionUnwindFunctionLeave is not called when ExceptionCatcherEnter is called:
        // https://github.com/dotnet/runtime/issues/10871
        if (!_nestedExceptionUnwindFunction.TryGetValue(Environment.CurrentManagedThreadId, out var count) || count <= 0)
        {
            Log($"Error: ExceptionCatcherEnter called without a matching ExceptionUnwindFunctionEnter");
            return HResult.E_FAIL;
        }

        count -= 1;
        _nestedExceptionUnwindFunction[Environment.CurrentManagedThreadId] = count;

        return HResult.S_OK;
    }

    protected override HResult ExceptionCatcherLeave()
    {
        if (!_nestedCatchBlocks.TryGetValue(Environment.CurrentManagedThreadId, out var count) || count <= 0)
        {
            Log($"Error: ExceptionCatcherLeave called without a matching ExceptionCatcherEnter");
            return HResult.E_FAIL;
        }

        count -= 1;
        _nestedCatchBlocks[Environment.CurrentManagedThreadId] = count;

        var threadId = ICorProfilerInfo.GetCurrentThreadId().ThrowIfFailed();

        Log($"ExceptionCatcherLeave - Thread {threadId} - Nested level {count}");

        return HResult.S_OK;
    }

    protected override HResult ExceptionCLRCatcherExecute()
    {
        Log("Error: the profiling API never raises the event ExceptionCLRCatcherExecute");
        return HResult.S_OK;
    }

    protected override HResult ExceptionCLRCatcherFound()
    {
        Log("Error: the profiling API never raises the event ExceptionCLRCatcherFound");
        return HResult.S_OK;
    }

    protected override unsafe HResult ExceptionOSHandlerEnter(nint* _)
    {
        Log("Error: the profiling API never raises the event ExceptionOSHandlerEnter");
        return HResult.S_OK;
    }

    protected override unsafe HResult ExceptionOSHandlerLeave(nint* _)
    {
        Log("Error: the profiling API never raises the event ExceptionOSHandlerEnter");
        return HResult.S_OK;
    }

    protected override HResult ExceptionSearchFilterEnter(FunctionId functionId)
    {
        var (_, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId).ThrowIfFailed();
        var metaDataImport = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport).ThrowIfFailed();
        var methodProperties = metaDataImport.GetMethodProps(new MdMethodDef(mdToken)).ThrowIfFailed();
        var (functionTypeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class).ThrowIfFailed();

        Log($"ExceptionSearchFilterEnter - {functionTypeName}.{methodProperties.Name}");

        _nestedExceptionSearchFilter.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, old) => old + 1);

        return HResult.S_OK;
    }

    protected override HResult ExceptionSearchFilterLeave()
    {
        if (!_nestedExceptionSearchFilter.TryGetValue(Environment.CurrentManagedThreadId, out var count) || count <= 0)
        {
            Log($"Error: ExceptionSearchFilterLeave called without a matching ExceptionSearchFilterEnter");
            return HResult.E_FAIL;
        }

        count -= 1;
        _nestedExceptionSearchFilter[Environment.CurrentManagedThreadId] = count;

        var threadId = ICorProfilerInfo.GetCurrentThreadId().ThrowIfFailed();

        Log($"ExceptionSearchFilterLeave - Thread {threadId} - Nested level {count}");

        return HResult.S_OK;
    }

    protected override HResult ExceptionSearchFunctionEnter(FunctionId functionId)
    {
        var (_, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId).ThrowIfFailed();
        var metaDataImport = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport).ThrowIfFailed();
        var methodProperties = metaDataImport.GetMethodProps(new MdMethodDef(mdToken)).ThrowIfFailed();
        var (functionTypeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class).ThrowIfFailed();

        Log($"ExceptionSearchFunctionEnter - {functionTypeName}.{methodProperties.Name}");

        _nestedExceptionSearchFunction.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, old) => old + 1);

        return HResult.S_OK;
    }

    protected override HResult ExceptionSearchFunctionLeave()
    {
        if (!_nestedExceptionSearchFunction.TryGetValue(Environment.CurrentManagedThreadId, out var count) || count <= 0)
        {
            Log($"Error: ExceptionSearchFunctionLeave called without a matching ExceptionSearchFilterEnter");
            return HResult.E_FAIL;
        }

        count -= 1;
        _nestedExceptionSearchFunction[Environment.CurrentManagedThreadId] = count;

        var threadId = ICorProfilerInfo.GetCurrentThreadId().ThrowIfFailed();

        Log($"ExceptionSearchFunctionLeave - Thread {threadId} - Nested level {count}");

        return HResult.S_OK;
    }
    protected override HResult ExceptionUnwindFinallyEnter(FunctionId functionId)
    {
        var (_, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId).ThrowIfFailed();
        var metaDataImport = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport).ThrowIfFailed();
        var methodProperties = metaDataImport.GetMethodProps(new MdMethodDef(mdToken)).ThrowIfFailed();
        var (functionTypeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class).ThrowIfFailed();

        Log($"ExceptionUnwindFinallyEnter - {functionTypeName}.{methodProperties.Name}");

        _nestedExceptionUnwindFinally.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, old) => old + 1);

        return HResult.S_OK;
    }

    protected override HResult ExceptionUnwindFinallyLeave()
    {
        if (!_nestedExceptionUnwindFinally.TryGetValue(Environment.CurrentManagedThreadId, out var count) || count <= 0)
        {
            Log($"Error: ExceptionUnwindFinallyLeave called without a matching ExceptionSearchFilterEnter");
            return HResult.E_FAIL;
        }

        count -= 1;
        _nestedExceptionUnwindFinally[Environment.CurrentManagedThreadId] = count;

        var threadId = ICorProfilerInfo.GetCurrentThreadId().ThrowIfFailed();

        Log($"ExceptionUnwindFinallyLeave - Thread {threadId} - Nested level {count}");

        return HResult.S_OK;
    }

    protected override HResult ExceptionUnwindFunctionEnter(FunctionId functionId)
    {
        var (_, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId).ThrowIfFailed();
        var metaDataImport = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport).ThrowIfFailed();
        var methodProperties = metaDataImport.GetMethodProps(new MdMethodDef(mdToken)).ThrowIfFailed();
        var (functionTypeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class).ThrowIfFailed();

        Log($"ExceptionUnwindFunctionEnter - {functionTypeName}.{methodProperties.Name}");

        _nestedExceptionUnwindFunction.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, old) => old + 1);

        return HResult.S_OK;
    }

    protected override HResult ExceptionUnwindFunctionLeave()
    {
        if (!_nestedExceptionUnwindFunction.TryGetValue(Environment.CurrentManagedThreadId, out var count) || count <= 0)
        {
            Log($"Error: ExceptionUnwindFunctionLeave called without a matching ExceptionSearchFilterEnter");
            return HResult.E_FAIL;
        }

        count -= 1;
        _nestedExceptionUnwindFunction[Environment.CurrentManagedThreadId] = count;

        var threadId = ICorProfilerInfo.GetCurrentThreadId().ThrowIfFailed();

        Log($"ExceptionUnwindFunctionLeave - Thread {threadId} - Nested level {count}");

        return HResult.S_OK;
    }

    protected override HResult ExceptionThrown(ObjectId thrownObjectId)
    {
        Log($"ExceptionThrown - {GetTypeNameFromObjectId(thrownObjectId)}");
        return HResult.S_OK;
    }

    protected override HResult FinalizeableObjectQueued(COR_PRF_FINALIZER_FLAGS finalizerFlags, ObjectId objectID)
    {
        Log($"FinalizeableObjectQueued - {finalizerFlags} - {GetTypeNameFromObjectId(objectID)}");
        return HResult.S_OK;
    }
    protected override HResult HandleCreated(GCHandleId handleId, ObjectId initialObjectId)
    {
        string name;

        try
        {
            name = GetTypeNameFromObjectId(initialObjectId);
        }
        catch (Win32Exception)
        {
            return HResult.S_OK;
        }

        Log($"HandleCreated - {handleId} - {name}");

        return HResult.S_OK;
    }

    protected override HResult HandleDestroyed(GCHandleId handleId)
    {
        Log($"HandleDestroyed - {handleId}");
        return HResult.S_OK;
    }

    protected override unsafe HResult GarbageCollectionStarted(Span<bool> generationCollected, COR_PRF_GC_REASON reason)
    {
        var generations = new List<int>();

        for (int i = 0; i < generationCollected.Length; i++)
        {
            if (generationCollected[i])
            {
                generations.Add(i);
            }
        }

        var count = Interlocked.Increment(ref _garbageCollectionsInProgress);

        Log($"GarbageCollectionStarted - {string.Join(", ", generations)} - {reason} - {count}");

        return HResult.S_OK;
    }

    protected override HResult GarbageCollectionFinished()
    {
        var count = Interlocked.Decrement(ref _garbageCollectionsInProgress);

        if (count < 0)
        {
            Log("Error: GarbageCollectionFinished called without a matching GarbageCollectionStarted");
        }

        Log($"GarbageCollectionFinished - {count}");

        return HResult.S_OK;
    }

    protected override HResult Shutdown()
    {
        Console.WriteLine("[Profiler] *** Shutting down, dumping remaining logs ***");

        while (Logs.TryDequeue(out var log))
        {
            Console.WriteLine($"[Profiler] {log}");
        }

        return HResult.S_OK;
    }

    private static void LogHResult(string function, HResult hresult)
    {
        Log($"Call to {function} failed with code {hresult}");
    }

    private static void Error(string explanation)
    {
        Log($"Error: {explanation}");
    }

    private static void Log(string line)
    {
        Logs.Enqueue(line);

        // Console.WriteLine($"[Profiler] {line}");
    }

    private string GetTypeNameFromObjectId(ObjectId objectId)
    {
        var classId = ICorProfilerInfo.GetClassFromObject(objectId).ThrowIfFailed();
        return GetTypeNameFromClassId(classId);
    }

    private string GetTypeNameFromClassId(ClassId classId)
    {
        var (moduleId, typeDef) = ICorProfilerInfo.GetClassIdInfo(classId).ThrowIfFailed();
        var moduleMetadata = ICorProfilerInfo.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport).ThrowIfFailed();
        var (typeName, _, _) = moduleMetadata.GetTypeDefProps(typeDef).ThrowIfFailed();

        return typeName;
    }

    private string GetFunctionFullName(FunctionId functionId)
    {
        var (_, _, moduleId, mdToken) = ICorProfilerInfo2.GetFunctionInfo(functionId);
        var (_, metaDataImport) = ICorProfilerInfo2.GetModuleMetaData(moduleId, CorOpenFlags.ofRead, KnownGuids.IMetaDataImport);
        var (_, methodProperties) = metaDataImport.GetMethodProps(new MdMethodDef(mdToken));
        var (_, typeName, _, _) = metaDataImport.GetTypeDefProps(methodProperties.Class);

        return $"{typeName}.{methodProperties.Name}";
    }
}