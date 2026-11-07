using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace AstralPartyBattleLog.Net;

// 게임 메모리에서 읽는 것은 아래 경로뿐이다(허용목록, docs/QUEUE-SYNC.md "읽는 경로").
//   Core.Net.NetManager::_inst             부모 Tools.MonoSingletonProvider<NetManager>의 정적 필드
//     .RPC                                 Core.Net.RPCMsgManager
//     ._OnPushRPCMsg                       List<Core.Net.OnPushRPCMsgData>
//       ._size                             int
//       ._items                            배열 길이와 [0]만
//         [0].<CMDID>k__BackingField       int
//         [0].<DOWNSN>k__BackingField      long
// 항목 본문(PARAM)과 다른 필드는 읽지 않는다. 래퍼 객체, 게임 메서드 호출, 필드 값을 객체로 받는 읽기는 쓰지 않는다.
// AccessViolation은 .NET 6에서 잡히지 않는다. 메타데이터로 타입과 오프셋을 확인한 필드만 따라가는 것이 방어다.
internal static unsafe class QueueReader
{
    private enum Phase : byte { Resolving, Ready, Disabled }

    private const int RetryFrames = 60;
    private const int FieldStatic = 0x10;
    private const int FieldLiteral = 0x40;
    private const int TypeI4 = 0x08;
    private const int TypeI8 = 0x0A;
    private const int TypeClass = 0x12;
    private const int TypeGenericInst = 0x15;
    private const int TypeSzArray = 0x1D;

    private static ManualLogSource? _log;
    private static Phase _phase;
    private static int _retryIn;

    private static IntPtr _netManager, _rpcClass, _listClass, _dataClass;
    private static IntPtr _instField, _owner;
    private static INativeClassStruct? _ownerStruct;
    private static bool _ownerReady;
    private static int _rpcOffset, _listOffset, _itemsOffset, _sizeOffset, _cmdOffset, _downOffset;
    private static IntPtr _seenInst, _seenRpc, _seenList, _seenElem;

    public static void Init(ManualLogSource log) => _log = log;

    public static void Disable(string reason)
    {
        bool running = QueueTracker.Enabled;
        QueueTracker.Disable(reason);
        if (_phase == Phase.Disabled) return;
        _phase = Phase.Disabled;
        if (running) _log?.LogWarning($"[queue] 대기열 읽기를 이번 실행 동안 끈다: {reason}");
    }

    public static QueueObservation Read()
    {
        if (_phase == Phase.Disabled) return QueueObservation.Unreadable;
        if (_phase == Phase.Resolving && !Resolve()) return QueueObservation.Unreadable;

        // 정적 필드 저장소는 클래스 초기화 때 할당된다. 초기화 여부는 클래스 구조체의 비트만 읽는다.
        // 초기화를 직접 부르면 정적 생성자, 곧 게임 코드가 돈다.
        if (!_ownerReady)
        {
            if (_ownerStruct is not { InitializedAndNoError: true }) return QueueObservation.Unreadable;
            _ownerReady = true;
        }

        IntPtr inst = IntPtr.Zero;
        IL2CPP.il2cpp_field_static_get_value(_instField, &inst);
        if (!Verified(inst, _netManager, ref _seenInst)) return QueueObservation.Unreadable;
        IntPtr rpc = *(IntPtr*)(inst + _rpcOffset);
        if (!Verified(rpc, _rpcClass, ref _seenRpc)) return QueueObservation.Unreadable;
        IntPtr list = *(IntPtr*)(rpc + _listOffset);
        if (!Verified(list, _listClass, ref _seenList)) return QueueObservation.Unreadable;

        IntPtr items = *(IntPtr*)(list + _itemsOffset);
        int size = *(int*)(list + _sizeOffset);
        if (items == IntPtr.Zero || size < 0 || (uint)size > IL2CPP.il2cpp_array_length(items))
            return QueueObservation.Unreadable;
        if (size == 0) return new QueueObservation(QueueRead.Empty);

        // 배열 원소는 머리(klass, monitor, bounds, max_length) 뒤에서 시작한다.
        IntPtr elem = *(IntPtr*)(items + 4 * IntPtr.Size);
        if (!Verified(elem, _dataClass, ref _seenElem)) return QueueObservation.Unreadable;
        return new QueueObservation(QueueRead.Head, *(int*)(elem + _cmdOffset), *(long*)(elem + _downOffset), size);
    }

    // 선언 타입을 메타데이터로 확인한 필드라 그 자리에는 그 타입의 객체만 들어간다. 같은 주소에 다른 객체가 다시 들어와도
    // 타입은 같으므로, 레이아웃이 바뀐 것을 알아채는 클래스 비교는 주소가 바뀔 때만 한다. 잘못된 포인터를 막아 주지는 못한다.
    private static bool Verified(IntPtr obj, IntPtr klass, ref IntPtr seen)
    {
        if (obj == IntPtr.Zero) return false;
        if (obj == seen) return true;
        if (IL2CPP.il2cpp_object_get_class(obj) != klass) return false;
        seen = obj;
        return true;
    }

    // 로그인 전에는 핫업데이트 어셈블리가 없어 클래스를 못 찾는다. 찾을 때까지 기록 없이 다시 본다.
    private static bool Resolve()
    {
        if (--_retryIn > 0) return false;
        _retryIn = RetryFrames;
        if (!FindClasses()) return false;

        string? bad = Layout();
        if (bad is not null)
        {
            Disable("layout:" + bad);
            return false;
        }

        _ownerStruct = UnityVersionHandler.Wrap((Il2CppClass*)_owner);
        _phase = Phase.Ready;
        _log?.LogInfo($"[queue] 대기열 읽기 준비 완료 (RPC +0x{_rpcOffset:X}, _OnPushRPCMsg +0x{_listOffset:X}, "
                      + $"_items +0x{_itemsOffset:X}, _size +0x{_sizeOffset:X}, CMDID +0x{_cmdOffset:X}, DOWNSN +0x{_downOffset:X})");
        return true;
    }

    // 세 클래스는 같은 핫업데이트 어셈블리에 있다. NetManager가 있는 이미지에 나머지가 없으면 이름이 바뀐 것이라 끈다.
    private static bool FindClasses()
    {
        // Il2CppInterop은 개수 인자를 uint로 선언했지만 네이티브 함수는 size_t(8바이트)를 쓴다. 8바이트 자리를 넘겨 스택을 덮지 않게 한다.
        ulong count = 0;
        IntPtr* assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref Unsafe.As<ulong, uint>(ref count));
        if (assemblies == null) return false;

        string? missing = null;
        for (ulong i = 0; i < count; i++)
        {
            IntPtr assembly = assemblies[i];
            if (assembly == IntPtr.Zero) continue;
            IntPtr image = IL2CPP.il2cpp_assembly_get_image(assembly);
            if (image == IntPtr.Zero) continue;
            IntPtr net = IL2CPP.il2cpp_class_from_name(image, "Core.Net", "NetManager");
            if (net == IntPtr.Zero) continue;
            IntPtr rpc = IL2CPP.il2cpp_class_from_name(image, "Core.Net", "RPCMsgManager");
            IntPtr data = IL2CPP.il2cpp_class_from_name(image, "Core.Net", "OnPushRPCMsgData");
            if (rpc == IntPtr.Zero || data == IntPtr.Zero)
            {
                missing ??= rpc == IntPtr.Zero ? "RPCMsgManager" : "OnPushRPCMsgData";
                continue;
            }

            _netManager = net;
            _rpcClass = rpc;
            _dataClass = data;
            return true;
        }

        if (missing is not null) Disable("layout:class:" + missing);
        return false;
    }

    // 클래스는 있는데 필드가 없거나 타입이 다르면 게임이 바뀐 것이라 이번 실행 동안 끈다.
    private static string? Layout()
    {
        if (!FindInst()) return "_inst";

        if (!InstanceField(_netManager, "RPC", TypeClass, out IntPtr type, out _rpcOffset)
            || IL2CPP.il2cpp_class_from_type(type) != _rpcClass)
            return "RPC";

        // 목록과 원소는 참조 형식이어야 한다. 값 형식이면 그 자리에 객체 주소가 아니라 값이 그대로 들어 있어, 주소로 따라가면 AV가 난다.
        if (!InstanceField(_rpcClass, "_OnPushRPCMsg", TypeGenericInst, out type, out _listOffset)) return "_OnPushRPCMsg";
        _listClass = IL2CPP.il2cpp_class_from_type(type);
        if (_listClass == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(_listClass)) return "_OnPushRPCMsg";

        if (!InstanceField(_listClass, "_items", TypeSzArray, out type, out _itemsOffset)
            || IL2CPP.il2cpp_type_get_class_or_element_class(type) != _dataClass
            || IL2CPP.il2cpp_class_is_valuetype(_dataClass))
            return "_items";
        if (!InstanceField(_listClass, "_size", TypeI4, out _, out _sizeOffset)) return "_size";

        if (!InstanceField(_dataClass, "<CMDID>k__BackingField", TypeI4, out _, out _cmdOffset)) return "<CMDID>k__BackingField";
        if (!InstanceField(_dataClass, "<DOWNSN>k__BackingField", TypeI8, out _, out _downOffset)) return "<DOWNSN>k__BackingField";
        return null;
    }

    private static bool FindInst()
    {
        IntPtr k = _netManager;
        while (k != IntPtr.Zero)
        {
            IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(k, "_inst");
            if (f == IntPtr.Zero) return false;
            IntPtr owner = IL2CPP.il2cpp_field_get_parent(f);
            int flags = IL2CPP.il2cpp_field_get_flags(f);
            IntPtr type = IL2CPP.il2cpp_field_get_type(f);
            if ((flags & FieldStatic) != 0 && (flags & FieldLiteral) == 0 && type != IntPtr.Zero
                && IL2CPP.il2cpp_type_get_type(type) == TypeClass && IL2CPP.il2cpp_class_from_type(type) == _netManager)
            {
                _instField = f;
                _owner = owner;
                return owner != IntPtr.Zero;
            }
            k = owner == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_parent(owner);
        }
        return false;
    }

    private static bool InstanceField(IntPtr klass, string name, int typeCode, out IntPtr type, out int offset)
    {
        type = IntPtr.Zero;
        offset = 0;
        IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
        if (f == IntPtr.Zero || (IL2CPP.il2cpp_field_get_flags(f) & FieldStatic) != 0) return false;
        type = IL2CPP.il2cpp_field_get_type(f);
        if (type == IntPtr.Zero || IL2CPP.il2cpp_type_get_type(type) != typeCode) return false;

        // 인스턴스 필드 오프셋은 객체 머리(klass, monitor)를 포함한다. 그보다 작으면 메타데이터가 어긋난 것이다.
        uint at = IL2CPP.il2cpp_field_get_offset(f);
        if (at < 2 * IntPtr.Size || at > int.MaxValue) return false;
        offset = (int)at;
        return true;
    }
}
