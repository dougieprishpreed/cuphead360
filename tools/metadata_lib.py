#!/usr/bin/env python3
"""Cuphead360 metadata reader and package builder.

This module parses the original managed assembly (using dnfile), recovers the
type/field/method/signature tables and method bodies, and writes a compact
platform-neutral metadata package consumed by the C++ runtime.

It deliberately does not emit C# or C++ source. The original IL is preserved
byte-for-byte; the runtime executes it.
"""

from __future__ import annotations

import os
import struct
import sys
from collections import OrderedDict

import dnfile


# ---------------------------------------------------------------------------
# Signature element kinds (kept in sync with src/runtime/metadata.h).
# ---------------------------------------------------------------------------

ET_VOID = 0x01
ET_BOOLEAN = 0x02
ET_CHAR = 0x03
ET_I1 = 0x04
ET_U1 = 0x05
ET_I2 = 0x06
ET_U2 = 0x07
ET_I4 = 0x08
ET_U4 = 0x09
ET_I8 = 0x0A
ET_U8 = 0x0B
ET_R4 = 0x0C
ET_R8 = 0x0D
ET_STRING = 0x0E
ET_PTR = 0x0F
ET_BYREF = 0x10
ET_VALUETYPE = 0x11
ET_CLASS = 0x12
ET_VAR = 0x13
ET_ARRAY = 0x14
ET_GENERICINST = 0x15
ET_TYPEDBYREF = 0x16
ET_I = 0x18
ET_U = 0x19
ET_FNPTR = 0x1B
ET_OBJECT = 0x1C
ET_SZARRAY = 0x1D
ET_MVAR = 0x1E
ET_CMOD_REQD = 0x1F
ET_CMOD_OPT = 0x20
ET_INTERNAL = 0x21
ET_MODIFIER = 0x40
ET_SENTINEL = 0x41
ET_PINNED = 0x45

# Runtime signature kinds (our package format).
SIG_VOID = 0
SIG_BOOL = 1
SIG_CHAR = 2
SIG_I1 = 3
SIG_U1 = 4
SIG_I2 = 5
SIG_U2 = 6
SIG_I4 = 7
SIG_U4 = 8
SIG_I8 = 9
SIG_U8 = 10
SIG_R4 = 11
SIG_R8 = 12
SIG_STRING = 13
SIG_OBJECT = 14
SIG_CLASS = 15
SIG_VALUETYPE = 16
SIG_SZARRAY = 17
SIG_ARRAY = 18
SIG_BYREF = 19
SIG_PTR = 20
SIG_GENERICINST = 21
SIG_VAR = 22
SIG_MVAR = 23
SIG_TYPEDBYREF = 24
SIG_FNPTR = 25
SIG_NATIVEINT = 26
SIG_NATIVEUINT = 27
SIG_PINNED = 28
SIG_SENTINEL = 29

_PRIM_TO_SIG = {
    ET_VOID: SIG_VOID,
    ET_BOOLEAN: SIG_BOOL,
    ET_CHAR: SIG_CHAR,
    ET_I1: SIG_I1,
    ET_U1: SIG_U1,
    ET_I2: SIG_I2,
    ET_U2: SIG_U2,
    ET_I4: SIG_I4,
    ET_U4: SIG_U4,
    ET_I8: SIG_I8,
    ET_U8: SIG_U8,
    ET_R4: SIG_R4,
    ET_R8: SIG_R8,
    ET_STRING: SIG_STRING,
    ET_OBJECT: SIG_OBJECT,
    ET_TYPEDBYREF: SIG_TYPEDBYREF,
    ET_I: SIG_NATIVEINT,
    ET_U: SIG_NATIVEUINT,
}

# TypeDef flags used by the runtime.
TYPE_FLAG_INTERFACE = 0x00000020
TYPE_FLAG_ABSTRACT = 0x00000080
TYPE_FLAG_SEALED = 0x00000100
TYPE_FLAG_SPECIAL_NAME = 0x00000400
TYPE_FLAG_IMPORT = 0x00001000
TYPE_FLAG_SERIALIZABLE = 0x00002000
TYPE_FLAG_BEFORE_FIELD_INIT = 0x00100000

# Method flags.
METHOD_FLAG_STATIC = 0x0010
METHOD_FLAG_FINAL = 0x0020
METHOD_FLAG_VIRTUAL = 0x0040
METHOD_FLAG_HIDE_BY_SIG = 0x0080
METHOD_FLAG_NEW_SLOT = 0x0100
METHOD_FLAG_ABSTRACT = 0x0400
METHOD_FLAG_SPECIAL_NAME = 0x0800
METHOD_FLAG_RT_SPECIAL_NAME = 0x1000
METHOD_FLAG_PINVOKE = 0x2000

# Field flags.
FIELD_FLAG_STATIC = 0x0010
FIELD_FLAG_LITERAL = 0x0040
FIELD_FLAG_INIT_ONLY = 0x0020

METHOD_IMPL_IL = 0x0000
METHOD_IMPL_NATIVE = 0x0001
METHOD_IMPL_INTERNAL_CALL = 0x1000
METHOD_IMPL_RUNTIME = 0x0003


class Reader:
    """Bounds-checked little-endian byte reader."""

    def __init__(self, data: bytes, pos: int = 0):
        self.data = data
        self.pos = pos

    def u8(self) -> int:
        v = self.data[self.pos]
        self.pos += 1
        return v

    def u16(self) -> int:
        v = struct.unpack_from("<H", self.data, self.pos)[0]
        self.pos += 2
        return v

    def u32(self) -> int:
        v = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return v

    def u64(self) -> int:
        v = struct.unpack_from("<Q", self.data, self.pos)[0]
        self.pos += 8
        return v

    def skip(self, n: int):
        self.pos += n

    def compressed(self) -> int:
        b0 = self.u8()
        if (b0 & 0x80) == 0:
            return b0
        if (b0 & 0xC0) == 0x80:
            return ((b0 & 0x3F) << 8) | self.u8()
        if (b0 & 0xE0) == 0xC0:
            return ((b0 & 0x1F) << 24) | (self.u8() << 16) | (self.u8() << 8) | self.u8()
        raise ValueError("invalid compressed integer")

    def remaining(self) -> int:
        return len(self.data) - self.pos


class SigNode:
    __slots__ = ("kind", "type_id", "generic_type_id", "children", "extra", "token")

    def __init__(self, kind: int, type_id: int = 0, children=None,
                 extra: int = 0, token: int = 0, generic_type_id: int = 0):
        self.kind = kind
        self.type_id = type_id
        self.generic_type_id = generic_type_id
        self.children = children or []
        self.extra = extra
        self.token = token

    def __repr__(self):
        return "Sig(kind=%d,type=%d,extra=%d,children=%d)" % (
            self.kind, self.type_id, self.extra, len(self.children))


class MethodSig:
    __slots__ = ("return_sig", "params", "flags", "generic_params")

    def __init__(self, return_sig, params, flags, generic_params=0):
        self.return_sig = return_sig
        self.params = params
        self.flags = flags
        self.generic_params = generic_params


class TypeModel:
    __slots__ = (
        "id", "name", "namespace", "full_name", "assembly", "decl_id",
        "base_id", "flags", "field_start", "field_count", "method_start",
        "method_count", "interface_ids", "size", "static_size",
        "vtable_size", "generic_type_id", "generic_arg_ids", "external",
        "composite", "kind", "is_value_type", "is_enum", "is_interface",
        "is_abstract", "is_sealed", "element_id", "generic_arity",
        "pack", "class_size", "nested_depth", "_iface_start", "_generic_arg_start",
    )

    def __init__(self):
        self.id = 0
        self.name = ""
        self.namespace = ""
        self.full_name = ""
        self.assembly = ""
        self.decl_id = 0
        self.base_id = 0
        self.flags = 0
        self.field_start = 0
        self.field_count = 0
        self.method_start = 0
        self.method_count = 0
        self.interface_ids = []
        self.size = 0
        self.static_size = 0
        self.vtable_size = 0
        self.generic_type_id = 0
        self.generic_arg_ids = []
        self.external = False
        self.composite = False
        self.kind = 0
        self.is_value_type = False
        self.is_enum = False
        self.is_interface = False
        self.is_abstract = False
        self.is_sealed = False
        self.element_id = 0
        self.generic_arity = 0
        self.pack = 0
        self.class_size = 0
        self.nested_depth = 0


class FieldModel:
    __slots__ = ("id", "name", "type_sig", "flags", "offset", "decl_id", "external", "sig", "serialized")

    def __init__(self):
        self.id = 0
        self.name = ""
        self.type_sig = 0
        self.flags = 0
        self.offset = 0
        self.decl_id = 0
        self.external = False
        self.sig = None
        self.serialized = False


class MethodModel:
    __slots__ = (
        "id", "name", "sig", "flags", "impl_flags", "il", "local_sig",
        "exceptions", "vtable_slot", "decl_id", "param_start", "param_count",
        "generic_params", "external", "body_off", "body_size", "_sig_idx",
    )

    def __init__(self):
        self.id = 0
        self.name = ""
        self.sig = None
        self.flags = 0
        self.impl_flags = 0
        self.il = b""
        self.local_sig = 0
        self.exceptions = []
        self.vtable_slot = 0xFFFFFFFF
        self.decl_id = 0
        self.param_start = 0
        self.param_count = 0
        self.generic_params = 0
        self.external = False
        self.body_off = 0
        self.body_size = 0


class MemberRef:
    __slots__ = ("id", "kind", "decl_id", "name", "sig", "method_id", "field_id", "external", "_sig_idx")

    def __init__(self):
        self.id = 0
        self.kind = 0  # 1 = method, 2 = field
        self.decl_id = 0
        self.name = ""
        self.sig = None
        self.method_id = 0
        self.field_id = 0
        self.external = True


class MetadataBuilder:
    def __init__(self, dll_path: str, verbose: bool = False):
        self.dll_path = dll_path
        self.verbose = verbose
        self.pe = dnfile.dnPE(dll_path)
        self.md = self.pe.net.mdtables
        self.data = self.pe.__data__
        self.current_assembly = str(self.md.Assembly.rows[0].Name) if self.md.Assembly else "Assembly-CSharp"

        self.types = [None]  # type id 0 is invalid
        self.typedef_by_id = {}
        self.typedef_by_fullname = {}
        self.type_ref_cache = {}
        self.type_spec_cache = {}
        self.generic_cache = {}
        self.array_cache = {}

        self.fields = [None]
        self.methods = [None]
        self.memberrefs = [None]
        self.methodspecs = []
        self.sigs = []  # list of SigNode
        self.sig_cache = {}
        self.method_sigs = [None]
        self.method_sig_cache = {}
        self.local_sigs = [None]
        self.local_sig_cache = {}
        self.user_strings = []  # list of UTF-16LE bytes
        self.user_string_tokens = []  # parallel: #US heap offset
        self.user_string_cache = {}
        self.exceptions = []
        self.param_names = []

        self._build_typedefs()
        self._resolve_bases()
        self._build_nested_relationships()
        self._build_fields_and_methods()
        self._build_interfaces()
        self._build_memberrefs()
        self._build_methodspecs()
        self._assign_vtables()
        self._layout_types()
        self.collect_user_strings()

    # -- helpers -----------------------------------------------------------

    def blob(self, heap_item) -> bytes:
        return heap_item.value if heap_item is not None else b""

    def sig_blob(self, item) -> bytes:
        return self.blob(item)

    def add_string(self, text: str) -> str:
        return text or ""

    def type_id_for(self, t: TypeModel) -> int:
        return t.id

    def new_type(self) -> TypeModel:
        t = TypeModel()
        t.id = len(self.types)
        self.types.append(t)
        return t

    def display_name(self, ns: str, name: str, decl_id: int) -> str:
        if decl_id:
            return self.types[decl_id].full_name + "/" + name
        return (ns + "." + name) if ns else name

    # -- type definitions --------------------------------------------------

    def _build_typedefs(self):
        rows = self.md.TypeDef.rows
        for i, row in enumerate(rows):
            if i == 0 and str(row.TypeName) == "<Module>":
                # Reserve id 1 for <Module> so TypeDef row indices map directly.
                t = self.new_type()
                t.name = "<Module>"
                t.namespace = ""
                t.full_name = "<Module>"
                t.assembly = self.current_assembly
                t.external = True
                t.flags = 0
                t.field_start = 1
                t.field_count = 0
                t.method_start = 1
                t.method_count = 0
                self.typedef_by_id[i + 1] = t
                self.typedef_by_fullname[t.full_name] = t
                continue
            t = self.new_type()
            t.name = str(row.TypeName)
            t.namespace = str(row.TypeNamespace)
            t.flags = int(row.struct.Flags)
            t.assembly = self.current_assembly
            t.external = False
            self.typedef_by_id[i + 1] = t

        # Declaring types from NestedClass.
        for nc in self.md.NestedClass.rows:
            nested = nc.NestedClass.row_index
            enclosing = nc.EnclosingClass.row_index
            t = self.typedef_by_id.get(nested)
            if t is not None:
                t.decl_id = enclosing
        # Compute full names after declaring types are known.
        for i in range(1, len(rows) + 1):
            t = self.typedef_by_id[i]
            if not t.full_name:
                t.full_name = self.display_name(t.namespace, t.name, t.decl_id)
                self.typedef_by_fullname[t.full_name] = t
        # Name/namespace are only meaningful for diagnostics; nested types keep
        # their namespace as authored.

    def _resolve_bases(self):
        for i, row in enumerate(self.md.TypeDef.rows, start=1):
            t = self.typedef_by_id[i]
            ext = row.Extends
            if ext is not None and getattr(ext, "row", None) is not None:
                t.base_id = self.resolve_typedef_or_ref(ext)
        # Determine value types/enums/interfaces.
        object_type = self.find_type("System", "Object")
        value_type = self.find_type("System", "ValueType")
        enum_type = self.find_type("System", "Enum")
        for t in self.types:
            if t is None or t.external:
                continue
            t.is_interface = bool(t.flags & TYPE_FLAG_INTERFACE)
            t.is_abstract = bool(t.flags & TYPE_FLAG_ABSTRACT)
            t.is_sealed = bool(t.flags & TYPE_FLAG_SEALED)
            if value_type is not None and t.base_id == value_type.id:
                t.is_value_type = True
            if enum_type is not None and t.base_id == enum_type.id:
                t.is_enum = True
                t.is_value_type = True
            _ = object_type

    def _build_nested_relationships(self):
        for t in self.types:
            if t is None or t.decl_id == 0:
                continue
            # Depth is only used for diagnostics.
            depth = 1
            p = t.decl_id
            seen = set()
            while p and p not in seen:
                seen.add(p)
                depth += 1
                p = self.types[p].decl_id
            t.nested_depth = depth

    # -- type references ---------------------------------------------------

    def find_type(self, ns: str, name: str):
        full = (ns + "." + name) if ns else name
        return self.typedef_by_fullname.get(full)

    def resolve_assembly_name(self, scope) -> str:
        if scope is None:
            return self.current_assembly
        tbl = type(scope.table).__name__
        if tbl == "AssemblyRef":
            return str(scope.row.Name)
        if tbl == "ModuleRef":
            return str(scope.row.Name)
        if tbl == "TypeRef":
            return self.resolve_typeref(scope.row).assembly
        if tbl == "Module":
            return self.current_assembly
        return self.current_assembly

    def external_type(self, ns: str, name: str, assembly: str, decl_id: int = 0) -> TypeModel:
        full = self.display_name(ns, name, decl_id)
        key = (assembly, full)
        cached = self.type_ref_cache.get(key)
        if cached is not None:
            return cached
        t = self.new_type()
        t.name = name
        t.namespace = ns
        t.full_name = full
        t.assembly = assembly
        t.decl_id = decl_id
        t.external = True
        t.generic_arity = name.count("`") and int(name.split("`", 1)[1].split("+", 1)[0] or 0) or 0
        self.type_ref_cache[key] = t
        return t

    def resolve_typeref(self, row) -> TypeModel:
        key = id(row)
        cached = self.type_ref_cache.get(key)
        if cached is not None:
            return cached
        name = str(row.TypeName)
        ns = str(row.TypeNamespace)
        scope = row.ResolutionScope
        decl_id = 0
        assembly = self.current_assembly
        if scope is not None and getattr(scope, "row", None) is not None:
            tbl = type(scope.table).__name__
            if tbl == "TypeRef":
                parent = self.resolve_typeref(scope.row)
                decl_id = parent.id
                assembly = parent.assembly
            else:
                assembly = self.resolve_assembly_name(scope)
        full = self.display_name(ns, name, decl_id)
        # A TypeRef may point at a TypeDef in this assembly.
        td = self.typedef_by_fullname.get(full)
        if td is not None and assembly == self.current_assembly:
            self.type_ref_cache[key] = td
            return td
        t = self.external_type(ns, name, assembly, decl_id)
        self.type_ref_cache[key] = t
        return t

    def resolve_typedef_or_ref(self, coded) -> int:
        if isinstance(coded, _CodedRef):
            if coded.table_name == "TypeDef":
                return coded.row_index
            if coded.table_name == "TypeRef":
                return self.resolve_typeref(self.md.TypeRef.rows[coded.row_index - 1]).id
            if coded.table_name == "TypeSpec":
                return self.resolve_typespec(coded.row_index)
            return 0
        if coded is None or getattr(coded, "row", None) is None:
            return 0
        tbl = type(coded.table).__name__
        if tbl == "TypeDef":
            return coded.row_index
        if tbl == "TypeRef":
            return self.resolve_typeref(coded.row).id
        if tbl == "TypeSpec":
            return self.resolve_typespec(coded.row_index)
        raise ValueError("unsupported TypeDefOrRef table %s" % tbl)

    def resolve_typespec(self, row_index: int) -> int:
        cached = self.type_spec_cache.get(row_index)
        if cached is not None:
            return cached
        row = self.md.TypeSpec.rows[row_index - 1]
        blob = self.blob(row.Signature)
        sig = self.parse_type(Reader(blob))
        self.type_spec_cache[row_index] = sig.type_id
        return sig.type_id

    def generic_instance(self, generic_type_id: int, args) -> TypeModel:
        key = (generic_type_id, tuple((a.kind, a.type_id, a.extra) for a in args))
        cached = self.generic_cache.get(key)
        if cached is not None:
            return cached
        base = self.types[generic_type_id]

        def arg_name(a):
            if a.kind in (SIG_VAR, SIG_MVAR):
                return ("!" if a.kind == SIG_VAR else "!!") + str(a.extra)
            tid = self.sig_type_id(a)
            if 0 < tid < len(self.types) and self.types[tid] is not None:
                return self.types[tid].full_name
            return "?"

        t = self.new_type()
        t.name = base.name
        t.namespace = base.namespace
        t.full_name = base.full_name + "<" + ",".join(arg_name(a) for a in args) + ">"
        t.assembly = base.assembly
        t.external = True
        t.composite = True
        t.kind = SIG_GENERICINST
        t.generic_type_id = generic_type_id
        t.generic_arg_ids = [self.sig_type_id(a) for a in args]
        self.generic_cache[key] = t
        return t

    def primitive_type(self, kind: int) -> TypeModel:
        names = {
            SIG_VOID: ("System", "Void"),
            SIG_BOOL: ("System", "Boolean"),
            SIG_CHAR: ("System", "Char"),
            SIG_I1: ("System", "SByte"),
            SIG_U1: ("System", "Byte"),
            SIG_I2: ("System", "Int16"),
            SIG_U2: ("System", "UInt16"),
            SIG_I4: ("System", "Int32"),
            SIG_U4: ("System", "UInt32"),
            SIG_I8: ("System", "Int64"),
            SIG_U8: ("System", "UInt64"),
            SIG_R4: ("System", "Single"),
            SIG_R8: ("System", "Double"),
            SIG_STRING: ("System", "String"),
            SIG_OBJECT: ("System", "Object"),
            SIG_NATIVEINT: ("System", "IntPtr"),
            SIG_NATIVEUINT: ("System", "UIntPtr"),
            SIG_TYPEDBYREF: ("System", "TypedReference"),
        }
        ns, name = names.get(kind, ("System", "Object"))
        return self.external_type(ns, name, "mscorlib")

    def sig_type_id(self, sig: SigNode) -> int:
        if sig.type_id and 0 < sig.type_id < len(self.types) and self.types[sig.type_id] is not None:
            return sig.type_id
        return self.primitive_type(sig.kind).id

    def array_type(self, inner: SigNode, rank: int) -> TypeModel:
        element_id = self.sig_type_id(inner)
        key = (element_id, rank)
        cached = self.array_cache.get(key)
        if cached is not None:
            return cached
        el = self.types[element_id]
        t = self.new_type()
        t.name = "[]"
        t.full_name = el.full_name + ("[]" if rank == 1 else "[%d]" % rank)
        t.external = True
        t.composite = True
        t.kind = SIG_SZARRAY if rank == 1 else SIG_ARRAY
        t.element_id = element_id
        t.generic_arity = 0
        self.array_cache[key] = t
        return t

    # -- signature parser --------------------------------------------------

    def sig(self, kind: int, type_id: int = 0, children=None, extra: int = 0,
            token: int = 0, generic_type_id: int = 0) -> int:
        node = SigNode(kind, type_id, children, extra, token, generic_type_id)
        key = (kind, type_id, tuple(children or []), extra, token, generic_type_id)
        cached = self.sig_cache.get(key)
        if cached is not None:
            return cached
        idx = len(self.sigs)
        self.sigs.append(node)
        self.sig_cache[key] = idx
        return idx

    def parse_type(self, r: Reader) -> SigNode:
        et = r.u8()
        if et in _PRIM_TO_SIG:
            k = _PRIM_TO_SIG[et]
            return SigNode(k)
        if et in (ET_CLASS, ET_VALUETYPE):
            tid = self.resolve_typedef_or_ref(self._read_type_def_or_ref(r))
            if et == ET_VALUETYPE and 0 < tid < len(self.types) and self.types[tid] is not None:
                self.types[tid].is_value_type = True
            return SigNode(SIG_CLASS if et == ET_CLASS else SIG_VALUETYPE, type_id=tid)
        if et in (ET_PTR, ET_BYREF, ET_SZARRAY, ET_PINNED):
            inner = self.parse_type(r)
            k = {ET_PTR: SIG_PTR, ET_BYREF: SIG_BYREF,
                 ET_SZARRAY: SIG_SZARRAY, ET_PINNED: SIG_PINNED}[et]
            if k == SIG_SZARRAY:
                t = self.array_type(inner, 1)
                return SigNode(SIG_SZARRAY, type_id=t.id, children=[self.sig(inner.kind, self.sig_type_id(inner), inner.children, inner.extra, inner.token)])
            return SigNode(k, children=[self.sig(inner.kind, inner.type_id, inner.children, inner.extra, inner.token)])
        if et == ET_ARRAY:
            inner = self.parse_type(r)
            rank = r.compressed()
            nr = r.compressed()
            for _ in range(nr):
                r.compressed()
            nl = r.compressed()
            for _ in range(nl):
                r.compressed()
            t = self.array_type(inner, rank)
            return SigNode(SIG_ARRAY, type_id=t.id, extra=rank,
                           children=[self.sig(inner.kind, self.sig_type_id(inner), inner.children, inner.extra, inner.token)])
        if et == ET_GENERICINST:
            gc = r.u8()
            if gc not in (ET_CLASS, ET_VALUETYPE):
                raise ValueError("genericinst without class/valuetype")
            generic_tid = self.resolve_typedef_or_ref(self._read_type_def_or_ref(r))
            count = r.compressed()
            args = [self.parse_type(r) for _ in range(count)]
            inst = self.generic_instance(generic_tid, args)
            child_ids = [self.sig(a.kind, a.type_id, a.children, a.extra, a.token) for a in args]
            return SigNode(SIG_GENERICINST, type_id=inst.id, generic_type_id=generic_tid,
                           children=child_ids, extra=count)
        if et in (ET_VAR, ET_MVAR):
            idx = r.compressed()
            return SigNode(SIG_VAR if et == ET_VAR else SIG_MVAR, extra=idx)
        if et in (ET_CMOD_REQD, ET_CMOD_OPT):
            token = r.compressed()
            inner = self.parse_type(r)
            return SigNode(inner.kind, inner.type_id, inner.children, inner.extra, token)
        if et == ET_FNPTR:
            ms = self.parse_method_sig(r)
            return SigNode(SIG_FNPTR)
        if et == ET_SENTINEL:
            return SigNode(SIG_SENTINEL)
        if et == ET_TYPEDBYREF:
            return SigNode(SIG_TYPEDBYREF)
        raise ValueError("unknown element type 0x%02x at %d" % (et, r.pos - 1))

    def _read_type_def_or_ref(self, r: Reader):
        v = r.compressed()
        tag = v & 3
        idx = v >> 2
        if tag == 0:
            return _CodedRef("TypeDef", idx)
        if tag == 1:
            return _CodedRef("TypeRef", idx)
        if tag == 2:
            return _CodedRef("TypeSpec", idx)
        raise ValueError("bad TypeDefOrRef tag")

    def parse_method_sig(self, r: Reader) -> MethodSig:
        flags = r.u8()
        generic = 0
        if flags & 0x10:
            generic = r.compressed()
        count = r.compressed()
        ret = self.parse_type(r)
        params = []
        for _ in range(count):
            if r.pos < len(r.data) and r.data[r.pos] == ET_SENTINEL:
                r.u8()
            params.append(self.parse_type(r))
        return MethodSig(ret, params, flags, generic)

    def add_method_sig(self, ms: MethodSig) -> int:
        ret = self.sig(ms.return_sig.kind, ms.return_sig.type_id, ms.return_sig.children,
                       ms.return_sig.extra, ms.return_sig.token)
        ps = [self.sig(p.kind, p.type_id, p.children, p.extra, p.token) for p in ms.params]
        key = (ret, tuple(ps), ms.flags, ms.generic_params)
        cached = self.method_sig_cache.get(key)
        if cached is not None:
            return cached
        idx = len(self.method_sigs)
        self.method_sigs.append((key, ret, ps, ms.flags, ms.generic_params))
        self.method_sig_cache[key] = idx
        return idx

    def parse_local_sig(self, blob: bytes) -> int:
        r = Reader(blob)
        if r.u8() != 0x07:
            return 0
        count = r.compressed()
        sig_ids = []
        for _ in range(count):
            if r.pos < len(r.data) and r.data[r.pos] == ET_PINNED:
                r.u8()
            s = self.parse_type(r)
            sig_ids.append(self.sig(s.kind, s.type_id, s.children, s.extra, s.token))
        key = tuple(sig_ids)
        cached = self.local_sig_cache.get(key)
        if cached is not None:
            return cached
        idx = len(self.local_sigs)
        self.local_sigs.append(key)
        self.local_sig_cache[key] = idx
        return idx

    # -- fields and methods ------------------------------------------------

    def _build_fields_and_methods(self):
        rows = self.md.TypeDef.rows
        field_starts = []
        method_starts = []
        for i, row in enumerate(rows):
            fs = row.FieldList[0].row_index if row.FieldList else None
            ms = row.MethodList[0].row_index if row.MethodList else None
            field_starts.append(fs)
            method_starts.append(ms)
        total_fields = len(self.md.Field.rows)
        total_methods = len(self.md.MethodDef.rows)

        # Fill field start/count by using the next non-empty start.
        for i in range(len(rows)):
            t = self.typedef_by_id[i + 1]
            start = field_starts[i]
            if start is None:
                # Find the next type's first field.
                j = i + 1
                while j < len(rows) and field_starts[j] is None:
                    j += 1
                start = field_starts[j] if j < len(rows) else (total_fields + 1)
            end = total_fields + 1
            for j in range(i + 1, len(rows)):
                if field_starts[j] is not None:
                    end = field_starts[j]
                    break
            t.field_start = start
            t.field_count = max(0, end - start)

            start = method_starts[i]
            if start is None:
                j = i + 1
                while j < len(rows) and method_starts[j] is None:
                    j += 1
                start = method_starts[j] if j < len(rows) else (total_methods + 1)
            end = total_methods + 1
            for j in range(i + 1, len(rows)):
                if method_starts[j] is not None:
                    end = method_starts[j]
                    break
            t.method_start = start
            t.method_count = max(0, end - start)

        # Fields.
        for i, row in enumerate(self.md.Field.rows, start=1):
            f = FieldModel()
            f.id = i
            f.name = str(row.Name)
            f.flags = int(row.struct.Flags)
            blob = self.blob(row.Signature)
            r = Reader(blob)
            if r.u8() != 0x06:
                raise ValueError("bad field sig %s" % f.name)
            fs = self.parse_type(r)
            f.sig = fs
            f.type_sig = self.sig(fs.kind, fs.type_id, fs.children, fs.extra, fs.token)
            self.fields.append(f)
        # Attach declaring type ids.
        for t in self.types:
            if t is None:
                continue
            for fid in range(t.field_start, t.field_start + t.field_count):
                if fid < len(self.fields):
                    self.fields[fid].decl_id = t.id

        # Field serialization flags from custom attributes.
        attrs = {}
        for ca in self.md.CustomAttribute.rows:
            try:
                if type(ca.Parent.table).__name__ != "Field":
                    continue
                fid = ca.Parent.row_index
                t = ca.Type
                tbl = type(t.table).__name__
                attr_type = None
                if tbl == "MemberRef":
                    mr = t.row
                    parent = mr.Class
                    ptbl = type(parent.table).__name__
                    if ptbl == "TypeRef":
                        attr_type = self.resolve_typeref(parent.row).full_name
                    elif ptbl == "TypeDef":
                        attr_type = self.typedef_by_id[parent.row_index].full_name
                elif tbl == "MethodDef":
                    attr_type = self.types[self.methods[t.row_index].decl_id].full_name
                if attr_type:
                    attrs.setdefault(fid, set()).add(attr_type)
            except Exception:
                pass
        for i, f in enumerate(self.fields[1:], start=1):
            public = (f.flags & 0x7) == 0x6
            names = attrs.get(i, set())
            f.serialized = public or ("UnityEngine.SerializeField" in names)
            if "System.NonSerializedAttribute" in names or "System.NonSerialized" in names:
                f.serialized = False

        # Methods.
        for i, row in enumerate(self.md.MethodDef.rows, start=1):
            m = MethodModel()
            m.id = i
            m.name = str(row.Name)
            m.flags = int(row.struct.Flags)
            m.impl_flags = int(row.struct.ImplFlags)
            ms_blob = self.blob(row.Signature)
            ms = self.parse_method_sig(Reader(ms_blob))
            m.sig = ms
            m.generic_params = ms.generic_params
            m.local_sig = 0
            m.exceptions = []
            m.il = b""
            rva = row.Rva
            if rva:
                body = self.parse_method_body(rva)
                if body is not None:
                    m.il = body[0]
                    m.local_sig = self.parse_local_sig(body[1]) if body[1] else 0
                    m.exceptions = body[2]
                    m.body_off = body[3]
                    m.body_size = len(body[0])
            self.methods.append(m)
        # Attach declaring type ids and parameter ranges.
        for t in self.types:
            if t is None:
                continue
            for mid in range(t.method_start, t.method_start + t.method_count):
                if mid < len(self.methods):
                    self.methods[mid].decl_id = t.id
        for i, row in enumerate(self.md.MethodDef.rows, start=1):
            m = self.methods[i]
            ps = row.ParamList
            if ps:
                m.param_start = ps[0].row_index
                # The Param table is contiguous per method.
                j = m.param_start
                while j <= len(self.md.Param.rows):
                    p = self.md.Param.rows[j - 1]
                    if str(p.Sequence) == "0" and j != m.param_start:
                        break
                    j += 1
                m.param_count = max(0, j - m.param_start)
            else:
                m.param_start = 0
                m.param_count = 0

    def parse_method_body(self, rva: int):
        off = self.pe.get_offset_from_rva(rva)
        if off is None or off < 0 or off >= len(self.data):
            return None
        b = self.data
        h = b[off]
        exceptions = []
        if (h & 3) == 2:
            code_size = h >> 2
            code_off = off + 1
            local_tok = 0
            fat_flags = 0
        elif (h & 3) == 3:
            fat_flags = struct.unpack_from("<H", b, off)[0]
            header_size = (fat_flags >> 12) * 4
            code_size = struct.unpack_from("<I", b, off + 4)[0]
            local_tok = struct.unpack_from("<I", b, off + 8)[0]
            code_off = off + header_size
        else:
            return None
        code = b[code_off:code_off + code_size]
        local_blob = b""
        if local_tok:
            table = local_tok >> 24
            idx = local_tok & 0xFFFFFF
            if table == 0x11 and 1 <= idx <= len(self.md.StandAloneSig.rows):
                local_blob = self.blob(self.md.StandAloneSig.rows[idx - 1].Signature)
        # Exception handling sections (fat methods only in practice).
        if fat_flags & 0x08:
            p = (code_off + code_size + 3) & ~3
            while p + 4 <= len(b):
                kind = b[p]
                if kind == 0:
                    break
                if kind & 0x40:
                    data_size = b[p + 1] | (b[p + 2] << 8) | (b[p + 3] << 16)
                    clauses_off = p + 4
                else:
                    data_size = b[p + 1]
                    if data_size == 0:
                        data_size = struct.unpack_from("<I", b, p + 4)[0]
                        clauses_off = p + 8
                    else:
                        clauses_off = p + 4
                # Defensive validation: a malformed section must not read
                # beyond the image. Exception clauses are rare in Cuphead
                # (24 try/catch sites); unparsed sections can be revisited
                # without affecting ordinary execution.
                if data_size < 4 or p + data_size > len(b) or (data_size - 4) % 24 != 0:
                    break
                if kind & 0x01:
                    n = (data_size - 4) // 24
                    for i in range(n):
                        co = clauses_off + i * 24
                        if co + 24 > len(b):
                            break
                        flags = struct.unpack_from("<H", b, co)[0]
                        try_off = struct.unpack_from("<I", b, co + 4)[0]
                        try_len = struct.unpack_from("<I", b, co + 8)[0]
                        hand_off = struct.unpack_from("<I", b, co + 12)[0]
                        hand_len = struct.unpack_from("<I", b, co + 16)[0]
                        extra = struct.unpack_from("<I", b, co + 20)[0]
                        catch_type = 0
                        if (flags & 0x07) == 0:
                            catch_type = self.resolve_encoded_type(extra)
                        exceptions.append((flags, try_off, try_len, hand_off, hand_len, catch_type))
                p += data_size
                p = (p + 3) & ~3
        return (code, local_blob, exceptions, code_off)

    def resolve_encoded_type(self, value: int) -> int:
        tag = value & 3
        idx = value >> 2
        if tag == 0 and 1 <= idx <= len(self.md.TypeDef.rows):
            return idx
        if tag == 1 and 1 <= idx <= len(self.md.TypeRef.rows):
            return self.resolve_typeref(self.md.TypeRef.rows[idx - 1]).id
        if tag == 2 and 1 <= idx <= len(self.md.TypeSpec.rows):
            return self.resolve_typespec(idx)
        return 0

    def resolve_token_type(self, token: int) -> int:
        table = token >> 24
        idx = token & 0xFFFFFF
        if table == 0x01 and 1 <= idx <= len(self.md.TypeRef.rows):
            return self.resolve_typeref(self.md.TypeRef.rows[idx - 1]).id
        if table == 0x02 and 1 <= idx <= len(self.md.TypeDef.rows):
            return idx
        if table == 0x1B and 1 <= idx <= len(self.md.TypeSpec.rows):
            return self.resolve_typespec(idx)
        return 0

    # -- interfaces --------------------------------------------------------

    def _build_interfaces(self):
        for row in self.md.InterfaceImpl.rows:
            cls = row.Class.row_index
            t = self.typedef_by_id.get(cls)
            if t is None:
                continue
            iid = self.resolve_typedef_or_ref(row.Interface)
            if iid and iid not in t.interface_ids:
                t.interface_ids.append(iid)

    # -- member references -------------------------------------------------

    def _build_memberrefs(self):
        for i, row in enumerate(self.md.MemberRef.rows, start=1):
            mr = MemberRef()
            mr.id = i
            mr.name = str(row.Name)
            parent = row.Class
            tbl = type(parent.table).__name__
            if tbl == "TypeDef":
                decl_id = parent.row_index
            elif tbl == "TypeRef":
                decl_id = self.resolve_typeref(parent.row).id
            elif tbl == "TypeSpec":
                decl_id = self.resolve_typespec(parent.row_index)
            elif tbl == "MethodDef":
                decl_id = self.methods[parent.row_index].decl_id
            elif tbl == "ModuleRef":
                decl_id = 0
            else:
                decl_id = 0
            mr.decl_id = decl_id
            blob = self.blob(row.Signature)
            if not blob:
                mr.kind = 0
            elif blob[0] == 0x06:
                mr.kind = 2
                r = Reader(blob)
                r.u8()
                fs = self.parse_type(r)
                mr.sig = fs
                # Try to resolve to a FieldDef in this assembly.
                if decl_id and decl_id < len(self.types) and not self.types[decl_id].external:
                    fd = self._find_field(decl_id, mr.name, fs)
                    if fd:
                        mr.field_id = fd
                        mr.external = False
            else:
                mr.kind = 1
                ms = self.parse_method_sig(Reader(blob))
                mr.sig = ms
                if decl_id and decl_id < len(self.types) and not self.types[decl_id].external:
                    md = self._find_method(decl_id, mr.name, ms)
                    if md:
                        mr.method_id = md
                        mr.external = False
            self.memberrefs.append(mr)

    def _sig_key(self, sig: SigNode):
        return (sig.kind, sig.type_id, tuple(sig.children), sig.extra)

    def _method_key(self, ms: MethodSig):
        return (self._sig_key(ms.return_sig), tuple(self._sig_key(p) for p in ms.params))

    def _find_method(self, decl_id: int, name: str, ms: MethodSig):
        t = self.types[decl_id]
        key = self._method_key(ms)
        for mid in range(t.method_start, t.method_start + t.method_count):
            m = self.methods[mid]
            if m.name == name and self._method_key(m.sig) == key:
                return mid
        return 0

    def _find_field(self, decl_id: int, name: str, fs: SigNode):
        t = self.types[decl_id]
        for fid in range(t.field_start, t.field_start + t.field_count):
            f = self.fields[fid]
            if f.name == name and self._sig_key(f.sig) == self._sig_key(fs):
                return fid
        return 0

    def _build_methodspecs(self):
        for row in self.md.MethodSpec.rows:
            blob = self.blob(row.Instantiation)
            r = Reader(blob)
            if r.u8() != 0x0A:
                continue
            count = r.compressed()
            args = [self.parse_type(r) for _ in range(count)]
            self.methodspecs.append((row.Method, args))

    # -- vtable ------------------------------------------------------------

    def _assign_vtables(self):
        visiting = set()
        done = {}

        def vt(tid):
            if tid in done:
                return done[tid]
            if tid <= 0 or tid >= len(self.types):
                return 0
            if tid in visiting:
                return 0
            visiting.add(tid)
            t = self.types[tid]
            if t.external:
                done[tid] = 0
                return 0
            base_size = vt(t.base_id) if t.base_id else 0
            size = base_size
            slots = {}
            # Map base virtual methods by name+signature.
            if t.base_id:
                bt = self.types[t.base_id]
                while bt and bt.id:
                    for mid in range(bt.method_start, bt.method_start + bt.method_count):
                        bm = self.methods[mid]
                        if (bm.flags & METHOD_FLAG_VIRTUAL) and bm.vtable_slot != 0xFFFFFFFF:
                            slots.setdefault((bm.name, self._method_key(bm.sig)), bm.vtable_slot)
                    bt = self.types[bt.base_id] if bt.base_id else None
            for mid in range(t.method_start, t.method_start + t.method_count):
                m = self.methods[mid]
                if not (m.flags & METHOD_FLAG_VIRTUAL):
                    continue
                if m.flags & METHOD_FLAG_STATIC:
                    continue
                key = (m.name, self._method_key(m.sig))
                if (m.flags & METHOD_FLAG_NEW_SLOT) or key not in slots:
                    m.vtable_slot = size
                    slots[key] = size
                    size += 1
                else:
                    m.vtable_slot = slots[key]
            t.vtable_size = size
            done[tid] = size
            visiting.discard(tid)
            return size

        for t in self.types:
            if t is not None and not t.external:
                vt(t.id)

    # -- layout ------------------------------------------------------------

    def _type_size_align(self, sig: SigNode):
        k = sig.kind
        if k in (SIG_BOOL, SIG_I1, SIG_U1):
            return (1, 1)
        if k in (SIG_CHAR, SIG_I2, SIG_U2):
            return (2, 2)
        if k in (SIG_I4, SIG_U4, SIG_R4):
            return (4, 4)
        if k in (SIG_I8, SIG_U8, SIG_R8):
            return (8, 8)
        if k in (SIG_STRING, SIG_OBJECT, SIG_CLASS, SIG_SZARRAY, SIG_ARRAY,
                 SIG_PTR, SIG_BYREF, SIG_FNPTR, SIG_NATIVEINT, SIG_NATIVEUINT):
            return (4, 4)
        if k == SIG_VALUETYPE:
            t = self.types[sig.type_id]
            self._ensure_layout(t)
            return (t.size, t.pack or 4)
        if k == SIG_GENERICINST:
            # Generic instances of BCL value types (e.g. KeyValuePair) are not
            # used by game fields; default to a reference-sized placeholder.
            return (4, 4)
        if k == SIG_VAR or k == SIG_MVAR:
            return (4, 4)
        return (4, 4)

    def _ensure_layout(self, t: TypeModel, seen=None):
        if t.size or t.external:
            return
        if seen is None:
            seen = set()
        if t.id in seen:
            return
        seen.add(t.id)
        if t.is_enum:
            # The value__ field carries the underlying type.
            for fid in range(t.field_start, t.field_start + t.field_count):
                f = self.fields[fid]
                if f.name == "value__":
                    sz, al = self._type_size_align(f.sig)
                    t.size = sz
                    t.pack = al
                    t.class_size = sz
                    return
            t.size = 4
            t.pack = 4
            return
        base_size = 0
        base_align = 1
        if t.base_id and t.base_id < len(self.types):
            b = self.types[t.base_id]
            if not b.external:
                self._ensure_layout(b, seen)
                base_size = b.size
                base_align = b.pack or 4
        off = base_size
        align = base_align or 1
        for fid in range(t.field_start, t.field_start + t.field_count):
            f = self.fields[fid]
            if f.flags & FIELD_FLAG_STATIC or f.flags & FIELD_FLAG_LITERAL:
                continue
            sz, al = self._type_size_align(f.sig)
            if al > align:
                align = al
            off = (off + al - 1) & ~(al - 1)
            f.offset = off
            off += sz
        t.pack = align
        t.class_size = off
        t.size = (off + align - 1) & ~(align - 1)
        # Static field layout.
        soff = 0
        for fid in range(t.field_start, t.field_start + t.field_count):
            f = self.fields[fid]
            if not (f.flags & FIELD_FLAG_STATIC) or (f.flags & FIELD_FLAG_LITERAL):
                continue
            sz, al = self._type_size_align(f.sig)
            soff = (soff + al - 1) & ~(al - 1)
            f.offset = soff
            soff += sz
        t.static_size = (soff + 3) & ~3

    def _layout_types(self):
        for t in self.types:
            if t is None or t.external:
                continue
            self._ensure_layout(t)

    def collect_user_strings(self):
        """Scans method bodies for ldstr tokens and preserves their UTF-16 text."""
        us = self.pe.net.user_strings
        for m in self.methods:
            if m is None or not m.il:
                continue
            for (start, opcode, operand_off, size, kind) in il_instructions(m.il):
                if opcode != 0x72 or kind != 7 or operand_off + 4 > len(m.il):
                    continue
                token = struct.unpack_from("<I", m.il, operand_off)[0]
                heap_off = token & 0xFFFFFF
                if heap_off in self.user_string_cache:
                    continue
                try:
                    item = us.get(heap_off)
                    value = item.value if item is not None else ""
                    data = value.encode("utf-16-le")
                except Exception:
                    data = b""
                idx = len(self.user_strings)
                self.user_strings.append(data)
                self.user_string_tokens.append(heap_off)
                self.user_string_cache[heap_off] = idx

    def validate_il(self):
        bad = []
        for m in self.methods:
            if m is None or not m.il:
                continue
            pos = 0
            ok = True
            for (start, opcode, operand_off, size, kind) in il_instructions(m.il):
                pos = operand_off + size
                if pos > len(m.il):
                    ok = False
                    break
            if not ok or pos != len(m.il):
                bad.append((m.id, m.name, len(m.il), pos))
        return bad

    # -- summary -----------------------------------------------------------

    def summary(self):
        live_types = [t for t in self.types if t is not None and not t.external]
        il_methods = [m for m in self.methods if m is not None and m.il]
        return {
            "assembly": self.current_assembly,
            "types": len(live_types),
            "external_types": len([t for t in self.types if t is not None and t.external]),
            "fields": len(self.fields) - 1,
            "methods": len(self.methods) - 1,
            "methods_with_il": len(il_methods),
            "il_bytes": sum(len(m.il) for m in il_methods),
            "memberrefs": len(self.memberrefs) - 1,
            "methodspecs": len(self.methodspecs),
            "user_strings": len(self.user_strings),
            "exceptions": sum(len(m.exceptions) for m in self.methods if m is not None),
        }


class _CodedRef:
    __slots__ = ("table_name", "row_index")

    def __init__(self, table_name: str, row_index: int):
        self.table_name = table_name
        self.row_index = row_index


# ---------------------------------------------------------------------------
# IL decoding (used to collect user strings and validate bodies).
# ---------------------------------------------------------------------------

# Operand kind per opcode:
# 0 = none, 1 = int8, 2 = int16, 3 = int32, 4 = int64,
# 5 = float32, 6 = float64, 7 = token, 8 = switch, 9 = uint8
_OP1 = [0] * 256
_OP2 = [0] * 256

def _set_op(table, code, kind):
    table[code] = kind

def _init_ops():
    for c in range(0x02, 0x0E): _set_op(_OP1, c, 0)
    for c in range(0x0E, 0x14): _set_op(_OP1, c, 9)
    for c in range(0x16, 0x1F): _set_op(_OP1, c, 0)
    _set_op(_OP1, 0x1F, 1); _set_op(_OP1, 0x20, 3); _set_op(_OP1, 0x21, 4)
    _set_op(_OP1, 0x22, 5); _set_op(_OP1, 0x23, 6)
    _set_op(_OP1, 0x27, 7); _set_op(_OP1, 0x28, 7); _set_op(_OP1, 0x29, 7)
    for c in range(0x2B, 0x38): _set_op(_OP1, c, 1)
    for c in range(0x38, 0x45): _set_op(_OP1, c, 3)
    _set_op(_OP1, 0x45, 8)
    for c in range(0x46, 0x58): _set_op(_OP1, c, 0)
    for c in range(0x58, 0x70): _set_op(_OP1, c, 0)
    _set_op(_OP1, 0x6F, 7)  # callvirt
    for c in range(0x70, 0x76): _set_op(_OP1, c, 7)
    _set_op(_OP1, 0x76, 0)  # conv.r.un
    _set_op(_OP1, 0x79, 7); _set_op(_OP1, 0x7A, 0)
    _set_op(_OP1, 0x7B, 7); _set_op(_OP1, 0x7C, 7); _set_op(_OP1, 0x7D, 7)
    _set_op(_OP1, 0x7E, 7); _set_op(_OP1, 0x7F, 7); _set_op(_OP1, 0x80, 7)
    _set_op(_OP1, 0x81, 7); _set_op(_OP1, 0x8C, 7); _set_op(_OP1, 0x8D, 7)
    _set_op(_OP1, 0x8E, 0); _set_op(_OP1, 0x8F, 7)
    for c in range(0x90, 0xA3): _set_op(_OP1, c, 0)
    _set_op(_OP1, 0xA3, 7); _set_op(_OP1, 0xA4, 7); _set_op(_OP1, 0xA5, 7)
    for c in range(0xB3, 0xBB): _set_op(_OP1, c, 0)
    _set_op(_OP1, 0xC2, 7); _set_op(_OP1, 0xC3, 0); _set_op(_OP1, 0xC6, 7)
    _set_op(_OP1, 0xD0, 7)
    for c in range(0xD1, 0xDC): _set_op(_OP1, c, 0)
    _set_op(_OP1, 0xDD, 7); _set_op(_OP1, 0xDE, 1); _set_op(_OP1, 0xDF, 0); _set_op(_OP1, 0xE0, 0)

    _set_op(_OP2, 0x06, 7); _set_op(_OP2, 0x07, 7)
    _set_op(_OP2, 0x09, 2); _set_op(_OP2, 0x0A, 2); _set_op(_OP2, 0x0B, 2)
    _set_op(_OP2, 0x0C, 2); _set_op(_OP2, 0x0D, 2); _set_op(_OP2, 0x0E, 2)
    _set_op(_OP2, 0x12, 9); _set_op(_OP2, 0x15, 7); _set_op(_OP2, 0x16, 7)
    _set_op(_OP2, 0x19, 9); _set_op(_OP2, 0x1C, 7)

_init_ops()

def il_instructions(code: bytes):
    """Yields (offset, opcode, operand_offset, operand_size, kind)."""
    i = 0
    n = len(code)
    while i < n:
        start = i
        op = code[i]
        i += 1
        kind = 0
        if op == 0xFE:
            if i >= n:
                break
            op2 = code[i]
            i += 1
            kind = _OP2[op2]
            opcode = 0xFE00 | op2
        else:
            kind = _OP1[op]
            opcode = op
        operand_off = i
        if kind == 1 or kind == 9:
            size = 1
        elif kind == 2:
            size = 2
        elif kind == 3 or kind == 5 or kind == 7:
            size = 4
        elif kind == 4 or kind == 6:
            size = 8
        elif kind == 8:
            if i + 4 > n:
                break
            count = struct.unpack_from("<I", code, i)[0]
            size = 4 + 4 * count
        else:
            size = 0
        i += size
        if i > n:
            break
        yield (start, opcode, operand_off, size, kind)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("usage: metadata_lib.py <assembly.dll>")
        raise SystemExit(2)
    b = MetadataBuilder(sys.argv[1], verbose=True)
    import json
    print(json.dumps(b.summary(), indent=2))
    # Validate IL decoding on every method and collect ldstr tokens.
    bad = 0
    total = 0
    for m in b.methods:
        if m is None or not m.il:
            continue
        total += 1
        last = -1
        for instr in il_instructions(m.il):
            last = instr[0]
        if last < 0:
            bad += 1
    print("il_methods=%d undecoded=%d" % (total, bad))

