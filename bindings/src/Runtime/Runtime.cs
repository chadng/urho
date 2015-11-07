//
// Runtime C# support
//
// Authors:
//   Miguel de Icaza (miguel@xamarin.com)
//
// Copyrigh 2015 Xamarin INc
//
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Urho
{
	public partial class Runtime
	{
		static readonly RefCountedCache RefCountedCache = new RefCountedCache();
		static Dictionary<System.Type, int> hashDict;
		static RefCountedEventCallback refCountedEventCallback;
		static ComponentEventCallback _componentEventCallback;

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void RefCountedEventCallback(IntPtr ptr, RefCountedEvent rcEvent);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void ComponentEventCallback(IntPtr ptr, SharpComponentEvent sce);

		[DllImport("mono-urho", CallingConvention = CallingConvention.Cdecl)]
		extern static void SetRefCountedEventCallback(RefCountedEventCallback callback);

		[DllImport("mono-urho", CallingConvention = CallingConvention.Cdecl)]
		extern static void SetComponentCallbacks(ComponentEventCallback callback);

		/// <summary>
		/// Runtime initialization. 
		/// </summary>
		public static void Initialize()
		{
			refCountedEventCallback = refCountedEventCallback ?? OnRefCountedEvent;
			_componentEventCallback = _componentEventCallback ?? OnSharpComponentEvent;
			SetRefCountedEventCallback(refCountedEventCallback);
			SetComponentCallbacks(_componentEventCallback);
		}

		/// <summary>
		/// This method is a workaround for iOS that requires all callback methods to be marked with a special attribute [MonoPInvokeCallback]
		/// </summary>
		public static void SetCustomNativeCallbacks(RefCountedEventCallback rcCallback, ComponentEventCallback cdCallback)
		{
			refCountedEventCallback = rcCallback;
			_componentEventCallback = cdCallback;
		}

		/// <summary>
		/// This method is called on each Serializable after the whole scene has been loaded
		/// Originally it's ApplyAttributes()
		/// </summary>
		public static void OnSharpComponentEvent(IntPtr ptr, SharpComponentEvent sce)
		{
			// it will trigger OnDeserilized if needed
			SharpComponent component = LookupObject<SharpComponent>(ptr);
			if (sce == SharpComponentEvent.Save)
			{
				component.ManagedState = component.OnSerializing() ?? string.Empty;
			}
		}

		/// <summary>
		/// This method is called by RefCounted::~RefCounted or RefCounted::AddRef
		/// </summary>
		public static void OnRefCountedEvent(IntPtr ptr, RefCountedEvent rcEvent)
		{
			if (rcEvent == RefCountedEvent.Delete)
			{
				var referenceHolder = RefCountedCache.Get(ptr);
				if (referenceHolder == null)
					return; //we don't have this object in the cache so let's just skip it

				var reference = referenceHolder.Reference;
				if (reference == null)
				{
					// seems like the reference was Weak and GC has removed it - remove item from the dictionary
					RefCountedCache.Remove(ptr);
				}
				else
				{
					reference.HandleNativeDelete();
				}
			}
			else if (rcEvent == RefCountedEvent.Addref)
			{
				//if we have an object with this handle and it's reference is weak - then change it to strong.
				var referenceHolder = RefCountedCache.Get(ptr);
				referenceHolder?.MakeStrong();
			}
		}

		public static T LookupRefCounted<T> (IntPtr ptr, bool createIfNotFound = true) where T:RefCounted
		{
			if (ptr == IntPtr.Zero)
				return null;

			var reference = RefCountedCache.Get(ptr)?.Reference;
			if (reference is T)
				return (T) reference;

			if (!createIfNotFound)
				return null;

			var refCounted = (T)Activator.CreateInstance(typeof(T), ptr);
			return refCounted;
		}

		public static T LookupObject<T>(IntPtr ptr, bool createIfNotFound = true) where T : UrhoObject
		{
			if (ptr == IntPtr.Zero)
				return null;

			var referenceHolder = RefCountedCache.Get(ptr);
			var reference = referenceHolder?.Reference;
			if (reference is T) //possible collisions
				return (T)reference;

			if (!createIfNotFound)
				return null;

			var name = Marshal.PtrToStringAnsi(UrhoObject.UrhoObject_GetTypeName(ptr));
			var type = Type.GetType("Urho." + name) ?? Type.GetType("Urho.Urho" + name); // "Urho.Urho" for remapped types like UrhoObjec, UrhoType
			var typeInfo = type.GetTypeInfo();
			if (typeInfo.IsSubclassOf(typeof(SharpComponent)) || type == typeof(SharpComponent))
			{
				//If we here it means that we are trying to get a user-defined component restored from file
				var state = SharpComponent.GetManagedStateForPtr(ptr);
				var result = (T)Activator.CreateInstance(Type.GetType(SharpComponent.GetTypeNameForPtr(ptr)), Application.Current.Context);
				var resultAsSharpComponent = (SharpComponent)(UrhoObject)result;
				resultAsSharpComponent.ManagedState = state;
				resultAsSharpComponent.OnDeserializing(state);
				return result;
			}

			var urhoObject = (T)Activator.CreateInstance(type, ptr);
			return urhoObject;
		}

		public static void UnregisterObject (IntPtr handle)
		{
			RefCountedCache.Remove(handle);
		}

		public static void RegisterObject (RefCounted refCounted)
		{
			RefCountedCache.Add(refCounted);
		}
		
		public static StringHash LookupStringHash (Type t)
		{
			if (hashDict == null)
				hashDict = new Dictionary<Type, int> ();

			int c;
			if (hashDict.TryGetValue (t, out c))
				return new StringHash (c);
			var typeStatic = t.GetRuntimeProperty("TypeStatic");
			if (typeStatic == null)
				throw new InvalidOperationException("The type doesn't have static TypeStatic property");

			var hash = (StringHash)typeStatic.GetValue(null);
			hashDict [t] = hash.Code;
			return hash;
		}

		[DllImport ("mono-urho", EntryPoint="Urho_GetPlatform", CallingConvention=CallingConvention.Cdecl)]
		extern static IntPtr GetPlatform();

		static string platform;
		public static string Platform => platform ?? (platform = Marshal.PtrToStringAnsi(GetPlatform()));

		static internal IReadOnlyList<T> CreateVectorSharedPtrProxy<T> (IntPtr handle) where T : UrhoObject
		{
			return new Vectors.ProxyUrhoObject<T> (handle);
		}

		static internal IReadOnlyList<T> CreateVectorSharedPtrRefcountedProxy<T>(IntPtr handle) where T : RefCounted
		{
			return new Vectors.ProxyRefCounted<T>(handle);
		}

		// for Debug purposes
		static internal int KnownObjectsCount => RefCountedCache.Count;
	}
}
