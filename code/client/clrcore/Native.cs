using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace CitizenFX.Core.Native
{
    public static class Function
    {
        public static T Call<T>(Hash hash, params InputArgument[] arguments)
        {
			object obj = InvokeInternal(hash, typeof(T), arguments);

			if (PointerArgumentSafety.ShouldClean((ulong)hash, typeof(T)))
			{
				return default;
			}

			return (T)obj;
        }

        public static void Call(Hash hash, params InputArgument[] arguments)
        {
            InvokeInternal(hash, typeof(void), arguments);
        }

		private static unsafe object InvokeInternal(Hash nativeHash, Type returnType, InputArgument[] args)
		{
			ScriptContext.Reset();

			foreach (var arg in args)
			{
				ScriptContext.Push(arg.Value);
			}

#if !IS_FXSERVER
			const int bufferCount = 32;

			// Note: direct access to argument buffer
			fixed (byte* p_functionData = ScriptContext.m_extContext.functionData)
			{
				ulong* argumentBuffer = (ulong*)p_functionData;
				ulong* initialValues = stackalloc ulong[bufferCount];
				// Buffer.MemoryCopy not available on client
				for (uint i = 0; i < bufferCount; ++i)
				{
					initialValues[i] = argumentBuffer[i];
				}
#else
			{
#endif

				ScriptContext.Invoke((ulong)nativeHash, InternalManager.ScriptHost);

				if (returnType == typeof(void))
				{
					return null;
				}

#if !IS_FXSERVER
				if (returnType == typeof(string) && argumentBuffer[0] != 0)
				{
					NativeStringResultSanitization(nativeHash, args, argumentBuffer, ScriptContext.m_extContext.numArguments, initialValues);
				}
#endif
				return ScriptContext.GetResult(returnType);
			}
		}

		/// <summary>
		/// Sanitization for string result types
		/// Loops through all values given by the ScRT and deny any that equals the result value which isn't of the string type
		/// </summary>
		/// <returns>Result from <see cref="ScriptContext.GetResult(Type)"/> or null if sanitized</returns>
		private static unsafe void NativeStringResultSanitization(Hash hash, InputArgument[] inputArguments, ulong* arguments, int numArguments, ulong* initialArguments)
		{
			var resultValue = arguments[0];

			// Step 1: quick compare all values until we found a hit
			// By not switching between all the buffers (incl. input arguments) we'll not introduce unnecessary cache misses.
			for (int a = 0; a < numArguments; ++a)
			{
				if (initialArguments[a] == resultValue)
				{
					// Step 2: loop our input list for as many times as `a` was increased
					int inputSize = inputArguments.Length;
					for (int i = 0; i < inputSize; ++i)
					{
						var csArg = inputArguments[i];

						// `a` can be reused by simply decrementing it, we'll go negative when we hit our goal as we decrement before checking (e.g.: `0 - 1 = -1` or `0 - 4 = -4`)
						switch (csArg?.Value)
						{
							case Vector2 v2:
								a -= 2;
								break;
							case Vector3 v3:
								a -= 3;
								break;
							case Vector4 v4:
							case Quaternion q:
								a -= 4;
								break;
							default:
								a--;
								break;
						}

						// string type is allowed
						if (a < 0)
						{
							if (csArg?.Value?.GetType() != typeof(string))
							{
								Debug.WriteLine($"Warning: Sanitized coerced string result for native {hash}");
								arguments[0] = 0;
							}

							return; // we found our arg, no more to check
						}
					}

					return; // found our value, no more to check
				}
			}
		}
	}

	[StructLayout(LayoutKind.Explicit)]
    internal struct NativeVector3
    {
		[FieldOffset(0)]
        public float X;

		[FieldOffset(8)]
		public float Y;

		[FieldOffset(16)]
		public float Z;

		public static implicit operator NativeVector3(Vector3 v)
		{
			return new NativeVector3() { X = v.X, Y = v.Y, Z = v.Z };
		}

        public static implicit operator Vector3(NativeVector3 self)
        {
            return new Vector3(self.X, self.Y, self.Z);
        }
    }

    internal static class MemoryAccess
    {
        /// <summary>
        /// Computes the Jenkins one-at-a-time hash used to resolve native command names.
        ///
        /// PERFORMANCE NOTE: The original implementation called <c>input.ToLowerInvariant()</c>
        /// before hashing, which allocates an entirely new heap string on every single call.
        /// Since native hash resolution happens extremely frequently (effectively any time a
        /// string-based native name is hashed rather than using a precomputed <see cref="Hash"/>),
        /// this was a significant, easily avoidable source of GC pressure in hot gameplay loops.
        ///
        /// Instead, we now perform the ASCII case-folding inline, character-by-character, using a
        /// simple bitwise OR (`c | 0x20`) to fold 'A'-'Z' to 'a'-'z' without any additional
        /// allocations. This is safe because native command names are guaranteed pure-ASCII
        /// identifiers, so we don't need full culture-aware lowering semantics here.
        /// </summary>
        public static uint GetHashKey(string input)
        {
            uint hash = 0;
            var len = input.Length;

            for (var i = 0; i < len; i++)
            {
                char c = input[i];

                // Fold ASCII uppercase -> lowercase without allocating a new string.
                if (c >= 'A' && c <= 'Z')
                {
                    c = (char)(c | 0x20);
                }

                hash += c;
                hash += (hash << 10);
                hash ^= (hash >> 6);
            }

            hash += (hash << 3);
            hash ^= (hash >> 11);
            hash += (hash << 15);

            return hash;
        }

        // Array.Empty<T>() returns a cached, immutable, zero-length array singleton for the given
        // type argument. Using `new int[0]` here previously allocated a fresh (albeit tiny) array
        // object on the heap every single time these accessors were evaluated — completely
        // unnecessary garbage for a value that's always semantically identical and immutable.
        public static int[] GetPickupObjectHandles() => Array.Empty<int>();
        public static int[] GetPedHandles() => Array.Empty<int>();
        public static int[] GetEntityHandles() => Array.Empty<int>();
        public static int[] GetPropHandles() => Array.Empty<int>();
        public static int[] GetVehicleHandles() => Array.Empty<int>();

        public static int[][] VehicleModels => null;

        public static float ReadWorldGravity()
        {
            return 0.0f;
        }

        public static void WriteWorldGravity(float f)
        {

        }

        [SecuritySafeCritical]
        public static byte ReadByte(IntPtr pointer)
        {
            return Marshal.ReadByte(pointer);
        }

        [SecuritySafeCritical]
        public static short ReadShort(IntPtr pointer)
        {
            return Marshal.ReadInt16(pointer);
        }

        [SecuritySafeCritical]
        public static int ReadInt(IntPtr pointer)
        {
            return Marshal.ReadInt32(pointer);
        }

        [SecuritySafeCritical]
        public static IntPtr ReadPtr(IntPtr pointer)
        {
            return Marshal.ReadIntPtr(pointer);
        }

        [SecuritySafeCritical]
        public static void WriteByte(IntPtr pointer, byte value)
        {
            Marshal.WriteByte(pointer, value);
        }

        [SecuritySafeCritical]
        public static void WriteShort(IntPtr pointer, short value)
        {
            Marshal.WriteInt16(pointer, value);
        }

        [SecuritySafeCritical]
        public static void WriteInt(IntPtr pointer, int value)
        {
            Marshal.WriteInt32(pointer, value);
        }

        /// <summary>
        /// Writes a raw 32-bit float directly to unmanaged memory.
        ///
        /// PERFORMANCE & SAFETY NOTE: The previous implementation routed through
        /// <c>BitConverter.GetBytes(value)</c>, which allocates a temporary
        /// <c>byte[4]</c> array on the managed heap for every single write — purely to
        /// reinterpret 4 bytes that are already sitting in a CPU register. Given this method is
        /// invoked continuously while poking at native engine memory (ped/vehicle/entity offsets),
        /// this was needless allocation churn multiplied by potentially millions of calls per frame.
        ///
        /// We now use <see cref="Unsafe.WriteUnaligned{T}(void*, T)"/> to reinterpret and write the
        /// bit pattern directly at the target address with zero heap traffic. `Unaligned` is used
        /// deliberately since these pointers originate from native engine structures that are not
        /// guaranteed to sit on 4-byte boundaries — using an aligned write here could otherwise
        /// trigger an alignment fault / crash on some platforms.
        /// </summary>
        [SecuritySafeCritical]
        public static unsafe void WriteFloat(IntPtr pointer, float value)
        {
            Unsafe.WriteUnaligned((void*)pointer, value);
        }

        /// <summary>
        /// Reads a raw 32-bit float directly from unmanaged memory.
        /// See <see cref="WriteFloat"/> for the rationale — this avoids the previous
        /// <c>BitConverter.ToSingle(BitConverter.GetBytes(...))</c> double-allocation/double-copy
        /// dance entirely, replacing it with a single unaligned raw memory read.
        /// </summary>
        [SecuritySafeCritical]
        public static unsafe float ReadFloat(IntPtr pointer)
        {
            return Unsafe.ReadUnaligned<float>((void*)pointer);
        }

        [SecuritySafeCritical]
        public static Matrix ReadMatrix(IntPtr pointer)
        {
            return Marshal.PtrToStructure<Matrix>(pointer);
        }

        [SecuritySafeCritical]
        public static Vector3 ReadVector3(IntPtr pointer)
        {
            return Marshal.PtrToStructure<Vector3>(pointer);
        }

        [SecuritySafeCritical]
        public static void WriteVector3(IntPtr pointer, Vector3 value)
        {
            Marshal.StructureToPtr(value, pointer, false);
        }

        [SecuritySafeCritical]
        public static bool IsBitSet(IntPtr pointer, int bit)
        {
            return _IsBitSet(pointer, bit);
        }

        [SecurityCritical]
        private static bool _IsBitSet(IntPtr pointer, int bit)
        {
            unsafe
            {
                var ptr = (int*)pointer.ToPointer();
                return (*ptr & (1 << bit)) != 0;
            }
        }

        [SecuritySafeCritical]
        public static void ClearBit(IntPtr pointer, int bit)
        {
            _ClearBit(pointer, bit);
        }

        [SecurityCritical]
        private static void _ClearBit(IntPtr pointer, int bit)
        {
            unsafe
            {
                var ptr = (int*)pointer.ToPointer();
                *ptr &= ~(1 << bit);
            }
        }

        [SecuritySafeCritical]
        public static void SetBit(IntPtr pointer, int bit)
        {
            _SetBit(pointer, bit);
        }

        [SecurityCritical]
        private static void _SetBit(IntPtr pointer, int bit)
        {
            unsafe
            {
                int* ptr = (int*)pointer.ToPointer();
                *ptr |= 1 << bit;
            }
        }

        private static IntPtr ms_stringString;

        public static IntPtr StringPtr
        {
            [SecuritySafeCritical]
            get
            {
                if (ms_stringString == IntPtr.Zero)
                {
                    ms_stringString = Marshal.StringToHGlobalAnsi("STRING");
                }

                return ms_stringString;
            }
        }

        private static IntPtr ms_nullString;

        public static IntPtr NullString
        {
            [SecuritySafeCritical]
            get
            {
                if (ms_nullString == IntPtr.Zero)
                {
                    ms_nullString = Marshal.StringToHGlobalAnsi("");
                }

                return ms_nullString;
            }
        }

		private static IntPtr ms_cellEmailBconString;

		public static IntPtr CellEmailBcon
		{
			[SecuritySafeCritical]
			get
			{
				if (ms_cellEmailBconString == IntPtr.Zero)
				{
					ms_cellEmailBconString = Marshal.StringToHGlobalAnsi("CELL_EMAIL_BCON");
				}

				return ms_cellEmailBconString;
			}
		}
    }

    public abstract class INativeValue
    {
        public abstract ulong NativeValue
        {
            get;
            set;
        }
    }

    public class InputArgument
    {
        protected object m_value;

        internal object Value => m_value;

        internal InputArgument(object value)
        {
            m_value = value;
        }

        public override string ToString()
        {
            return m_value.ToString();
        }

        public static implicit operator InputArgument(bool value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(sbyte value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(byte value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(short value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(ushort value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(int value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(uint value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(long value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(ulong value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(float value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(double value)
        {
            return new InputArgument((float)value);
        }

        public static implicit operator InputArgument(Enum value)
        {
            return new InputArgument(value);
        }

        public static implicit operator InputArgument(string value)
        {
            return new InputArgument(value);
        }

		public static implicit operator InputArgument(Vector3 value)
		{
			return new InputArgument(value);
		}

		public static implicit operator InputArgument(Delegate value)
		{
			return new InputArgument(InternalManager.CanonicalizeRef(FunctionReference.Create(value).Identifier));
		}

		[SecuritySafeCritical]
        public static implicit operator InputArgument(INativeValue value)
        {
            return new InputArgument(value.NativeValue);
        }

        [SecurityCritical]
        public static implicit operator InputArgument(IntPtr value)
        {
            return new InputArgument(value);
        }

        [SecurityCritical]
        public static unsafe implicit operator InputArgument(void* value)
        {
            return new InputArgument(new IntPtr(value));
        }
    }

	/// <summary>
	/// Represents a native "by-ref" output slot backed by a small unmanaged buffer.
	///
	/// MEMORY SAFETY NOTE: This type now implements <see cref="IDisposable"/> so unmanaged
	/// memory can be released deterministically the moment a resource script is done with it
	/// (e.g. wrapped in a `using` block). Previously, cleanup relied solely on the finalizer,
	/// which:
	///   1. Runs on the separate finalizer thread at a GC-determined (non-deterministic) time.
	///   2. Under sustained high-frequency native call volume (common in FiveM scripts creating
	///      many OutputArgument instances per tick), can cause the finalization queue to grow
	///      faster than it's drained, leading to memory bloat and unpredictable native handle
	///      lifetime — a real risk for use-after-free-style native memory corruption if a game
	///      thread native call outlives the object's expected buffer lifetime.
	///
	/// The finalizer is retained purely as a safety net for callers who forget to Dispose(),
	/// ensuring we never leak the underlying HGlobal allocation outright.
	/// </summary>
	public class OutputArgument : InputArgument, IDisposable
	{
		private readonly IntPtr m_dataPtr;
		private bool m_disposed;

		[SecuritySafeCritical]
		public OutputArgument()
			: base(AllocateData())
		{
			m_dataPtr = (IntPtr)m_value;
		}

		[SecuritySafeCritical]
		public OutputArgument(object arg)
			: this()
		{
			if (Marshal.SizeOf(arg.GetType()) > 8)
			{
				return;
			}

			Marshal.WriteInt64(m_dataPtr, 0, 0);
			Marshal.StructureToPtr(arg, m_dataPtr, false);
		}

		/// <summary>
		/// Deterministically frees the unmanaged output buffer and suppresses finalization.
		/// Always prefer calling this (or wrapping the instance in a `using` statement) in
		/// hot per-tick native call sites to avoid finalizer-thread GC pressure.
		/// </summary>
		[SecuritySafeCritical]
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		[SecuritySafeCritical]
		protected virtual void Dispose(bool disposing)
		{
			if (m_disposed)
			{
				return;
			}

			if (m_dataPtr != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(m_dataPtr);
			}

			m_disposed = true;
		}

		/// <summary>
		/// Fallback safety net only — fires if a caller forgets to call <see cref="Dispose()"/>.
		/// Do not rely on this in hot paths; it defers cleanup to the non-deterministic
		/// finalizer thread and contributes to finalization queue growth under load.
		/// </summary>
		[SecuritySafeCritical]
		~OutputArgument()
		{
			Dispose(false);
		}

		[SecuritySafeCritical]
		public T GetResult<T>()
		{
			return GetResultInternal<T>();
		}

		[SecurityCritical]
		private unsafe T GetResultInternal<T>()
		{
			var data = new byte[24];
			Marshal.Copy(m_dataPtr, data, 0, 24);

			// no native commands include `char**` or `scrObject**` arguments, so these are invalid here
			// see https://github.com/citizenfx/fivem/issues/1855
			//
			// this *might* break struct workarounds but these aren't considered as supported anyway
			if (typeof(T) == typeof(string) || typeof(T) == typeof(object))
			{
				return default(T);
			}

			fixed (byte* dataPtr = data)
			{
				return (T)ScriptContext.GetResult(typeof(T), dataPtr);
			}
		}

		[SecuritySafeCritical]
		private static IntPtr AllocateData()
		{
			return Marshal.AllocHGlobal(24);
		}
	}
}
