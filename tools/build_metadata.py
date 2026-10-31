#!/usr/bin/env python3
"""Builds GameData/metadata.chm from a Cuphead Assembly-CSharp.dll.

The output is a compact, little-endian package consumed by the C++ runtime on
both Windows and Xbox 360. The original IL is preserved exactly.
"""

from __future__ import annotations

import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from metadata_lib import (
    MetadataBuilder, SigNode, SIG_CLASS, SIG_VALUETYPE, SIG_GENERICINST,
    SIG_SZARRAY, SIG_ARRAY, SIG_BYREF, SIG_PTR, SIG_VAR, SIG_MVAR,
    SIG_PINNED, SIG_SENTINEL, METHOD_FLAG_STATIC, il_instructions, Reader,
)

MAGIC = 0x43484D31  # "CHM1"
VERSION = 1
HEADER_WORDS = 80
HEADER_SIZE = HEADER_WORDS * 4

# Section tags in header order.
SECTION_NAMES = [
    "strings", "types", "fields", "methods", "sigs", "method_sigs",
    "local_sigs", "il", "exceptions", "user_strings", "user_string_bytes",
    "memberrefs", "methodspecs", "params", "interface_ids",
    "generic_arg_ids", "sig_children", "method_param_sigs",
    "local_var_sigs", "methodspec_args", "field_rvas", "field_rva_bytes",
    "field_constants",
]


class StringPool:
    def __init__(self):
        self.data = bytearray(b"\x00")
        self.offsets = {"": 0}

    def add(self, s: str) -> int:
        if s is None:
            s = ""
        if s in self.offsets:
            return self.offsets[s]
        off = len(self.data)
        self.data.extend(s.encode("utf-8"))
        self.data.append(0)
        self.offsets[s] = off
        return off


class PackageWriter:
    def __init__(self, builder: MetadataBuilder):
        self.b = builder
        self.strings = StringPool()

        self.sig_children = []
        self.method_param_sigs = []
        self.local_var_sigs = []
        self.interface_ids = []
        self.generic_arg_ids = []
        self.methodspec_args = []

        self.sig_records = []
        self.method_sig_records = []
        self.local_sig_records = []
        self.user_records = []
        self.memberref_records = []
        self.methodspec_records = []
        self.param_records = []
        self.field_rva_records = []
        self.rewritten_il = {}

        self._prepare()

    def s(self, value: str) -> int:
        return self.strings.add(value)

    def _register_sigs(self):
        b = self.b
        # Register every method signature and member-ref signature before the
        # signature tables are flattened. add_method_sig() may append a new
        # signature, so doing this first keeps all indices valid.
        for m in b.methods[1:]:
            m._sig_idx = b.add_method_sig(m.sig)
        for mr in b.memberrefs[1:]:
            if mr.kind == 1 and mr.sig is not None:
                mr._sig_idx = b.add_method_sig(mr.sig)
            elif mr.kind == 2 and mr.sig is not None:
                mr._sig_idx = b.sig(mr.sig.kind, mr.sig.type_id, mr.sig.children,
                                    mr.sig.extra, mr.sig.token)
            else:
                mr._sig_idx = 0

    def _prepare(self):
        b = self.b
        self._register_sigs()
        # Rewrite tokens now, before the type table is serialized. Token
        # resolution can create composite type records for TypeSpecs, and the
        # header type count must include every record actually written.
        for m in b.methods[1:]:
            self.rewritten_il[m.id] = self._rewrite_il(m)
        # Signatures: flatten child arrays and emit records in id order.
        for i in range(len(b.sigs)):
            sig = b.sigs[i]
            child_start = len(self.sig_children)
            for c in sig.children:
                self.sig_children.append(c)
            self.sig_records.append((sig.kind, sig.type_id, child_start,
                                     len(sig.children), sig.extra, sig.token))
        # Method signatures.
        for i in range(1, len(b.method_sigs)):
            entry = b.method_sigs[i]
            if entry is None:
                continue
            key, ret, params, flags, generic_params = entry
            start = len(self.method_param_sigs)
            self.method_param_sigs.extend(params)
            self.method_sig_records.append((ret, start, len(params), flags))
        # Local signatures.
        for i in range(1, len(b.local_sigs)):
            entry = b.local_sigs[i]
            if entry is None:
                continue
            start = len(self.local_var_sigs)
            self.local_var_sigs.extend(entry)
            self.local_sig_records.append((start, len(entry)))
        # Type auxiliary arrays.
        for t in b.types:
            if t is None:
                continue
            start = len(self.interface_ids)
            self.interface_ids.extend(t.interface_ids)
            # Replace interface ids with start/count in the record writer.
            t._iface_start = start
            start = len(self.generic_arg_ids)
            self.generic_arg_ids.extend(t.generic_arg_ids)
            t._generic_arg_start = start
        # User strings.
        utf16 = bytearray()
        for i, data in enumerate(b.user_strings):
            off = len(utf16)
            utf16.extend(data)
            length = len(data) // 2
            self.user_records.append((b.user_string_tokens[i], off, length))
        self.user_bytes = bytes(utf16)
        # MemberRefs.
        for mr in b.memberrefs[1:]:
            if mr.kind == 1:
                resolved = mr.method_id
            elif mr.kind == 2:
                resolved = mr.field_id
            else:
                resolved = 0
            flags = 0 if mr.external else 1
            self.memberref_records.append((mr.kind, mr.decl_id, self.s(mr.name),
                                           mr._sig_idx, resolved, flags))
        # MethodSpecs.
        for (method, args) in b.methodspecs:
            start = len(self.methodspec_args)
            for a in args:
                self.methodspec_args.append(b.sig(a.kind, b.sig_type_id(a), a.children, a.extra, a.token))
            # Preserve the MemberRef row for external generic calls such as
            # GameObject.GetComponent<T>(); MethodDef rows keep their id.
            try:
                table_name = type(method.table).__name__
                if table_name == "MethodDef":
                    mid = method.row_index
                    mrid = 0
                elif table_name == "MemberRef":
                    mid = 0
                    mrid = method.row_index
                else:
                    mid = 0
                    mrid = 0
            except Exception:
                mid = 0
                mrid = 0
            self.methodspec_records.append((mid, start, len(args), mrid))
        # Param names.
        for i in range(len(b.md.Param.rows)):
            row = b.md.Param.rows[i]
            self.param_records.append((self.s(str(row.Name)), int(row.struct.Flags)))

    def _rewrite_il(self, m):
        """Rewrites type/field/method tokens to canonical TypeDef/FieldDef/
        MethodDef tokens where possible. Branch offsets are unchanged because
        every metadata token is four bytes."""
        b = self.b
        code = bytearray(m.il)
        type_ops = {
            0x74, 0x75, 0x79, 0x8C, 0x8D, 0x8F, 0xA3, 0xA4, 0xA5,
            0xFE15, 0xFE16, 0xFE1C,
        }
        method_ops = {0x27, 0x28, 0x6F, 0x73, 0xFE06, 0xFE07}
        field_ops = {0x7B, 0x7C, 0x7D, 0x7E, 0x7F, 0x80}
        for (start, opcode, operand_off, size, kind) in il_instructions(code):
            if kind != 7 or operand_off + 4 > len(code):
                continue
            token = struct.unpack_from("<I", code, operand_off)[0]
            table = token >> 24
            if opcode == 0xD0 and table == 0x1B:
                idx = token & 0xFFFFFF
                if 1 <= idx <= len(b.md.TypeSpec.rows):
                    try:
                        row = b.md.TypeSpec.rows[idx - 1]
                        sig = b.parse_type(Reader(b.blob(row.Signature)))
                    except Exception:
                        sig = None
                    if sig is not None and sig.kind in (SIG_MVAR, SIG_VAR):
                        marker = 0x7F000000 if sig.kind == SIG_MVAR else 0x7E000000
                        struct.pack_into("<I", code, operand_off,
                                         marker | ((sig.extra & 0xFFFFFF) + 1))
                        continue
            if opcode in type_ops or (opcode == 0xD0 and table in (0x01, 0x1B)):
                tid = b.resolve_token_type(token)
                if tid:
                    struct.pack_into("<I", code, operand_off, 0x02000000 | tid)
            elif opcode in method_ops and table == 0x0A:
                idx = token & 0xFFFFFF
                if 1 <= idx < len(b.memberrefs):
                    mr = b.memberrefs[idx]
                    if mr.kind == 1 and mr.method_id:
                        struct.pack_into("<I", code, operand_off, 0x06000000 | mr.method_id)
            elif opcode in field_ops and table == 0x0A:
                idx = token & 0xFFFFFF
                if 1 <= idx < len(b.memberrefs):
                    mr = b.memberrefs[idx]
                    if mr.kind == 2 and mr.field_id:
                        struct.pack_into("<I", code, operand_off, 0x04000000 | mr.field_id)
        return bytes(code)

    def _pack(self, path: str):
        b = self.b

        def pack_sig(rec):
            return struct.pack("<6I", *rec)

        def pack_method_sig(rec):
            return struct.pack("<4I", *rec)

        def pack_local(rec):
            return struct.pack("<2I", *rec)

        types = bytearray()
        for t in b.types:
            if t is None:
                continue
            iface_start = getattr(t, "_iface_start", 0)
            gen_start = getattr(t, "_generic_arg_start", 0)
            types.extend(struct.pack(
                "<32I",
                self.s(t.name), self.s(t.namespace), self.s(t.full_name),
                self.s(t.assembly), t.flags, t.base_id, t.decl_id,
                t.field_start, t.field_count, t.method_start, t.method_count,
                iface_start, len(t.interface_ids), t.size, t.static_size,
                t.vtable_size, t.generic_type_id, gen_start, len(t.generic_arg_ids),
                t.element_id, 1 if t.external else 0, 1 if t.composite else 0,
                1 if t.is_value_type else 0, 1 if t.is_enum else 0,
                1 if t.is_interface else 0, 1 if t.is_abstract else 0,
                1 if t.is_sealed else 0, t.kind, t.class_size, t.pack,
                t.generic_arity, 0,
            ))

        fields = bytearray()
        for f in b.fields[1:]:
            fields.extend(struct.pack("<6I", self.s(f.name), f.type_sig, f.flags,
                                      f.offset, f.decl_id, 0))

        il = bytearray()
        exceptions = bytearray()
        methods = bytearray()
        for m in b.methods[1:]:
            il_off = len(il)
            il.extend(self.rewritten_il[m.id])
            exc_start = len(exceptions) // 24
            for e in m.exceptions:
                exceptions.extend(struct.pack("<6I", *e))
            sig_idx = m._sig_idx
            methods.extend(struct.pack(
                "<16I", self.s(m.name), sig_idx, m.flags, m.impl_flags,
                il_off, len(m.il), m.local_sig, exc_start, len(m.exceptions),
                m.vtable_slot, m.decl_id, m.param_start, m.param_count,
                m.generic_params, 0, 0,
            ))

        sigs = b"".join(pack_sig(r) for r in self.sig_records)
        method_sigs = b"".join(pack_method_sig(r) for r in self.method_sig_records)
        local_sigs = b"".join(pack_local(r) for r in self.local_sig_records)
        user_strings = b"".join(struct.pack("<3I", *r) for r in self.user_records)
        memberrefs = b"".join(struct.pack("<6I", *r) for r in self.memberref_records)
        methodspecs = b"".join(struct.pack("<4I", *r) for r in self.methodspec_records)
        params = b"".join(struct.pack("<2I", *r) for r in self.param_records)

        field_rva_bytes = bytearray()
        for fid, data in b.field_rvas:
            off = len(field_rva_bytes)
            field_rva_bytes.extend(data)
            while len(field_rva_bytes) & 15:
                field_rva_bytes.append(0)
            self.field_rva_records.append((fid, off, len(data)))
        field_rvas = b"".join(struct.pack("<3I", *r) for r in self.field_rva_records)

        field_constants = b"".join(
            struct.pack("<3I", fid, int(value) & 0xFFFFFFFF,
                        (int(value) >> 32) & 0xFFFFFFFF)
            for fid, value in sorted(b.field_constants.items()))

        def u32arr(values):
            return b"".join(struct.pack("<I", v) for v in values)

        # All record writers above intern their names; only now is the string
        # pool complete and safe to freeze.
        strings = bytes(self.strings.data)

        sections = {
            "strings": strings,
            "types": bytes(types),
            "fields": bytes(fields),
            "methods": bytes(methods),
            "sigs": sigs,
            "method_sigs": method_sigs,
            "local_sigs": local_sigs,
            "il": bytes(il),
            "exceptions": bytes(exceptions),
            "user_strings": user_strings,
            "user_string_bytes": self.user_bytes,
            "memberrefs": memberrefs,
            "methodspecs": methodspecs,
            "params": params,
            "interface_ids": u32arr(self.interface_ids),
            "generic_arg_ids": u32arr(self.generic_arg_ids),
            "sig_children": u32arr(self.sig_children),
            "method_param_sigs": u32arr(self.method_param_sigs),
            "local_var_sigs": u32arr(self.local_var_sigs),
            "methodspec_args": u32arr(self.methodspec_args),
            "field_rvas": field_rvas,
            "field_rva_bytes": bytes(field_rva_bytes),
            "field_constants": field_constants,
        }

        # Header layout: magic/version/file_size + counts + section offsets.
        type_count = sum(1 for t in b.types if t is not None)
        if len(types) != type_count * 128:
            raise RuntimeError("type table mismatch: %d records for count %d" % (len(types) // 128, type_count))
        counts = {
            "type_count": type_count,
            "field_count": len(b.fields) - 1,
            "method_count": len(b.methods) - 1,
            "sig_count": len(self.sig_records),
            "method_sig_count": len(self.method_sig_records),
            "local_sig_count": len(self.local_sig_records),
            "user_string_count": len(self.user_records),
            "memberref_count": len(self.memberref_records),
            "methodspec_count": len(self.methodspec_records),
            "exception_count": len(exceptions) // 24,
            "param_count": len(self.param_records),
            "field_rva_count": len(self.field_rva_records),
            "field_constant_count": len(b.field_constants),
        }

        header = [MAGIC, VERSION, 0]  # file_size patched below
        # Order must match the C++ MetadataPackageHeader.
        order = [
            "type_count", "field_count", "method_count", "sig_count",
            "method_sig_count", "local_sig_count", "user_string_count",
            "memberref_count", "methodspec_count", "exception_count",
            "param_count",
            "interface_id_count", "generic_arg_id_count", "sig_child_count",
            "method_param_sig_count", "local_var_sig_count",
            "methodspec_arg_count", "field_rva_count", "field_constant_count",
        ]
        extra_counts = {
            "interface_id_count": len(self.interface_ids),
            "generic_arg_id_count": len(self.generic_arg_ids),
            "sig_child_count": len(self.sig_children),
            "method_param_sig_count": len(self.method_param_sigs),
            "local_var_sig_count": len(self.local_var_sigs),
            "methodspec_arg_count": len(self.methodspec_args),
        }
        for k in order:
            header.append(counts.get(k, extra_counts.get(k, 0)))
        # Section offsets start at word 24; sizes follow all section offsets.
        while len(header) < 24:
            header.append(0)

        body = bytearray()
        offsets = {}
        for name in SECTION_NAMES:
            data = sections[name]
            offsets[name] = HEADER_SIZE + len(body)
            body.extend(data)
            while len(body) & 15:
                body.append(0)

        # Header offsets in section order, then sizes.
        off_words = [offsets[name] for name in SECTION_NAMES]
        size_words = [len(sections[name]) for name in SECTION_NAMES]
        header.extend(off_words)
        header.extend(size_words)
        while len(header) < HEADER_WORDS:
            header.append(0)
        assert len(header) == HEADER_WORDS, len(header)
        header[2] = HEADER_SIZE + len(body)
        blob = struct.pack("<%dI" % HEADER_WORDS, *header) + bytes(body)
        with open(path, "wb") as f:
            f.write(blob)
        return len(blob)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("assembly", help="path to Assembly-CSharp.dll")
    ap.add_argument("output", help="output metadata.chm")
    args = ap.parse_args()
    b = MetadataBuilder(args.assembly)
    bad = b.validate_il()
    if bad:
        print("IL validation failed for %d methods" % len(bad))
        for x in bad[:5]:
            print(" ", x)
        return 1
    w = PackageWriter(b)
    size = w._pack(args.output)
    print("wrote %s (%d bytes)" % (args.output, size))
    print("types=%d fields=%d methods=%d il=%d sigs=%d user_strings=%d" % (
        len(b.types) - 1, len(b.fields) - 1, len(b.methods) - 1,
        sum(len(m.il) for m in b.methods if m), len(w.sig_records),
        len(w.user_records)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
