﻿//Copyright (c) 2016, Samuel Pollachini (Samuel Polacchini)
//This project is licensed under the terms of the MIT license

using UnityEngine;
using UnityEngine.Assertions;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;

namespace UnityForCpp
{

//Singleton, meant to be component of an UnityForCpp object on the Unity Editor, which is not destroyed with the scene
//Provides Unity/C# multiplatform features to its correspoding C++ class (UnityAdapter.h/.cpp)
//For C# users it providers the shared memory arrays being used by C++
//
//WARNING: your C++ code is responsible for releasing all the shared arrays at each game replay. You may do that by 
//warning your C++ code about the execution end, and so calling UnityAdapter::DestroyReleaseAllManagedArrays from there.
//Since the DLL is not reloaded at each replay of the game for an unique execution of the Unity Editor, 
//you must watch for side effects coming from previous replays on your C++ execution state. 
public class UnityAdapter : MonoBehaviour
{
    //SINGLETON access point 
    public static UnityAdapter instance { get { return _s_instance; } }

    //Get an existing shared array being used by the cpp code. The id should be given to you from your cpp application
    public T[] GetSharedArray<T>(int id)
    {
        return s_sharedArrays[id].GetArrayAsObject() as T[];
    }

    private static UnityAdapter _s_instance = null;

    //Usually fields have to be static since methods to be called from C++ must be static
    private static Dictionary<int, SharedArrayHolder> s_sharedArrays = null;
    private static int s_lastSharedArrayId = -1;

    //WARNING: the C++ DLL state is not reset when the game is reset on the Unity Editor
    //So, ideally, DLL initialization functions should support redundant calls with no side effects 
    private void Awake()
    {
        if (_s_instance != null && _s_instance != this)
        {
            Destroy(this.gameObject);
            throw new Exception("[UnityForCpp] More than one instance of UnityAdapter being used!!");
        }
        else
        {
            _s_instance = this;
            DontDestroyOnLoad(this.gameObject);
        }

        if (s_sharedArrays == null)
            s_sharedArrays = new Dictionary<int, SharedArrayHolder>();

        Debug.Log("[UnityForCpp] UnityForCpp DLL is about to be loaded.");

        //Provide C# function pointers to cpp, making Unity features available from cpp code
        UnityAdapterDLL.UA_SetOutputDebugStrFcPtr(OutputDebugStr);
        UnityAdapterDLL.UA_SetFileFcPtrs(RequestFileContent, SaveTextFile);
        UnityAdapterDLL.UA_SetArrayFcPtrs(RequestManagedArray, ReleaseManagedArray);

        Debug.Log("[UnityForCpp] UnityForCpp DLL was loaded and UnityAdapter was initialized at the C++ and C# sides.");
    }

    //Provides Unity Debug.Log feature to the cpp code
    [MonoPInvokeCallback(typeof(UnityAdapterDLL.OutputDebugStrDelegate))]
    static void OutputDebugStr(string str)
    {
        Debug.Log("[From Cpp] " + str);
    }

    //Provides access to files managed by Unity to the c++ code
    //fullFilePath may (and should) include the file extension
    //Looks first for existing saved files and after for bundle files 
    //Saved files will have the persistent data folder as path root 
    //Bundle files are expected to have an Assets "Resources" folder as path root
    [MonoPInvokeCallback(typeof(UnityAdapterDLL.RequestFileContentDelegate))]
    static void RequestFileContent(string fullFilePath)
    {
        string finalFilePath = Application.persistentDataPath + "/" + fullFilePath;
        byte[] fileData = null;
        if (File.Exists(finalFilePath))
            fileData = File.ReadAllBytes(finalFilePath);
        else
        {
            //Remove the file extension as requested by Resources.Load
            string directoryPath = Path.GetDirectoryName(fullFilePath);
            if (directoryPath != null && directoryPath.Length > 0)
                finalFilePath = directoryPath + "/" + Path.GetFileNameWithoutExtension(fullFilePath);
            else
                finalFilePath = Path.GetFileNameWithoutExtension(fullFilePath);

            TextAsset fileAsAsset = (TextAsset)Resources.Load(finalFilePath) as TextAsset;
            if (fileAsAsset)
                fileData = fileAsAsset.bytes;
        }

        if (fileData != null) 
        {
            //if the file was found and loaded deliver it to cpp as a shared managed byte array
            //to be released by the C++ code after usage, in the same way other shared arrays are.
            int arrayId = ++s_lastSharedArrayId;
            SharedArrayHolder sharedArrayHolder = new SharedArrayHolder(fileData);
            s_sharedArrays.Add(arrayId, sharedArrayHolder);
            UnityAdapterDLL.UA_DeliverManagedArray(arrayId, sharedArrayHolder.GetArrayPtr(), fileData.Length);
        }
        else //else we finish the function without delivering anything
            Debug.Log("[UnityForCpp] Warning: Could NOT provide the file " + finalFilePath + ", requested from the C++ code");
    }

    //Provides the Unity multiplatform file saving feature to the C++ code
    //Save the "textContent" to a new file created based on the provided path (the directory is also created if needed)
    //The persistent data folder for the platform will be the path root. An existing file for this path will be overwriten.
    //Use "DELETE" as the "textContent" for just deleting the file for this path if it exists. 
    [MonoPInvokeCallback(typeof(UnityAdapterDLL.SaveTextFileDelegate))]
    static void SaveTextFile(string fullFilePath, string textContent)
    {
        fullFilePath = Application.persistentDataPath + "/" + fullFilePath;
        if (textContent.StartsWith("DELETE"))
        {
            if (File.Exists(fullFilePath))
                File.Delete(fullFilePath);
        }
        else
        {
            FileInfo fileInfo = new FileInfo(fullFilePath);
            fileInfo.Directory.Create();
            File.WriteAllBytes(fullFilePath, System.Text.Encoding.ASCII.GetBytes(textContent));
        }
    }

    //Provides C# managed arrays to be shared with the C++ code. The C++ MUST release it through ReleaseManagedArray
    //Type is specified by its .NET type name (https://msdn.microsoft.com/en-us/library/ya5y69ds.aspx)
    //Be sure you can handle this type properly on C++ from the platforms you are working on
    //For Visual C++ (windows platform): https://msdn.microsoft.com/en-us/library/0wf2yk2k.aspx
    [MonoPInvokeCallback(typeof(UnityAdapterDLL.RequestManagedArrayDelegate))]
    static void RequestManagedArray(string typeName, int arraySize)
    {
        Type arrayType = Type.GetType(typeName);
        Assert.IsTrue(arrayType != null, "[UnityForCpp] The C++ code has requested an array of an invalid type.");

        SharedArrayHolder sharedArrayHolder = new SharedArrayHolder(Array.CreateInstance(arrayType, arraySize));

        int arrayId = ++s_lastSharedArrayId;
        UnityAdapterDLL.UA_DeliverManagedArray(arrayId, sharedArrayHolder.GetArrayPtr(), arraySize);
        s_sharedArrays.Add(arrayId, sharedArrayHolder);
    }

    //To be used from the C++ code. 
    //Release C# shared arrays requested via RequestManagedArray or via RequestFileContent
    [MonoPInvokeCallback(typeof(UnityAdapterDLL.ReleaseManagedArrayDelegate))]
    static void ReleaseManagedArray(int arrayId)
    {
        SharedArrayHolder sharedArrayHolder = s_sharedArrays[arrayId];
        sharedArrayHolder.Release();
        s_sharedArrays.Remove(arrayId);
    }

    //Class to hold the arrays currently allocated and shared with the C++ code
    private class SharedArrayHolder
    {
        private object m_arrayAsObject;
        private GCHandle m_handle;

        public SharedArrayHolder(object array)
        {
            m_arrayAsObject = array;
            m_handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        }

        public object GetArrayAsObject()
        {
            return m_arrayAsObject;
        }

        public IntPtr GetArrayPtr()
        {
            return m_handle.AddrOfPinnedObject();
        }

        public void Release()
        {
            m_handle.Free();
        }
    }; // SharedArrayHolder class

#if (UNITY_WEBGL || UNITY_IOS) && !(UNITY_EDITOR)
    const string DLL_NAME = "__Internal";
#else
    const string DLL_NAME = "UnityForCpp";
#endif

    //Expose C++ DLL methods related to the UnityAdapter C++ code to this C# class
    private class UnityAdapterDLL
    {
        //Delegates corresponding to the function pointers defined at UnityAdapter.h

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OutputDebugStrDelegate(string str);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RequestFileContentDelegate(string fullFilePath);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SaveTextFileDelegate(string fullFilePath, string textContent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RequestManagedArrayDelegate(string typeName, int arraySize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReleaseManagedArrayDelegate(int arrayId);

        //Functions defined at UnityAdapterPlugin.h

        [DllImport(DLL_NAME)]
        public static extern void UA_SetOutputDebugStrFcPtr(OutputDebugStrDelegate dlg);

        [DllImport(DLL_NAME)]
        public static extern void UA_SetFileFcPtrs(RequestFileContentDelegate requestFileDlg, SaveTextFileDelegate saveTextFileDelegate);

        [DllImport(DLL_NAME)]
        public static extern void UA_SetArrayFcPtrs(RequestManagedArrayDelegate requestDlg, ReleaseManagedArrayDelegate releaseDlg);

        [DllImport(DLL_NAME)]
        public static extern void UA_DeliverManagedArray(int id, System.IntPtr array, int size);
    }; //UnityAdapterDLL class

} // UnityAdapter class

} // UnityForCpp namespace