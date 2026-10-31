#ifndef CH360_RUNTIME_METADATA_H
#define CH360_RUNTIME_METADATA_H

#include "core/containers.h"
#include "core/types.h"

namespace ch360 {

// Signature kinds. Kept in sync with tools/metadata_lib.py.
enum SigKind {
    SIG_VOID = 0,
    SIG_BOOL = 1,
    SIG_CHAR = 2,
    SIG_I1 = 3,
    SIG_U1 = 4,
    SIG_I2 = 5,
    SIG_U2 = 6,
    SIG_I4 = 7,
    SIG_U4 = 8,
    SIG_I8 = 9,
    SIG_U8 = 10,
    SIG_R4 = 11,
    SIG_R8 = 12,
    SIG_STRING = 13,
    SIG_OBJECT = 14,
    SIG_CLASS = 15,
    SIG_VALUETYPE = 16,
    SIG_SZARRAY = 17,
    SIG_ARRAY = 18,
    SIG_BYREF = 19,
    SIG_PTR = 20,
    SIG_GENERICINST = 21,
    SIG_VAR = 22,
    SIG_MVAR = 23,
    SIG_TYPEDBYREF = 24,
    SIG_FNPTR = 25,
    SIG_NATIVEINT = 26,
    SIG_NATIVEUINT = 27,
    SIG_PINNED = 28,
    SIG_SENTINEL = 29
};

enum TypeFlags {
    TYPE_FLAG_INTERFACE = 0x00000020,
    TYPE_FLAG_ABSTRACT = 0x00000080,
    TYPE_FLAG_SEALED = 0x00000100,
    TYPE_FLAG_SPECIAL_NAME = 0x00000400,
    TYPE_FLAG_IMPORT = 0x00001000,
    TYPE_FLAG_SERIALIZABLE = 0x00002000,
    TYPE_FLAG_BEFORE_FIELD_INIT = 0x00100000
};

enum MethodFlags {
    METHOD_FLAG_STATIC = 0x0010,
    METHOD_FLAG_FINAL = 0x0020,
    METHOD_FLAG_VIRTUAL = 0x0040,
    METHOD_FLAG_HIDE_BY_SIG = 0x0080,
    METHOD_FLAG_NEW_SLOT = 0x0100,
    METHOD_FLAG_ABSTRACT = 0x0400,
    METHOD_FLAG_SPECIAL_NAME = 0x0800,
    METHOD_FLAG_RT_SPECIAL_NAME = 0x1000,
    METHOD_FLAG_PINVOKE = 0x2000
};

enum FieldFlags {
    FIELD_FLAG_STATIC = 0x0010,
    FIELD_FLAG_INIT_ONLY = 0x0020,
    FIELD_FLAG_LITERAL = 0x0040
};

struct TypeInfo {
    const char* name;
    const char* ns;
    const char* full_name;
    const char* assembly;
    u32 flags;
    u32 base_id;
    u32 decl_id;
    u32 field_start;
    u32 field_count;
    u32 method_start;
    u32 method_count;
    u32 interface_start;
    u32 interface_count;
    u32 size;
    u32 static_size;
    u32 vtable_size;
    u32 generic_type_id;
    u32 generic_arg_start;
    u32 generic_arg_count;
    u32 element_id;
    u32 external;
    u32 composite;
    u32 is_value_type;
    u32 is_enum;
    u32 is_interface;
    u32 is_abstract;
    u32 is_sealed;
    u32 kind;
    u32 class_size;
    u32 pack;
    u32 generic_arity;
};

struct FieldInfo {
    const char* name;
    u32 type_sig;
    u32 flags;
    u32 offset;
    u32 decl_id;
};

struct MethodInfo {
    const char* name;
    u32 sig;
    u32 flags;
    u32 impl_flags;
    u32 il_offset;
    u32 il_size;
    u32 local_sig;
    u32 exception_start;
    u32 exception_count;
    u32 vtable_slot;
    u32 decl_id;
    u32 param_start;
    u32 param_count;
    u32 generic_params;
};

struct SigInfo {
    u32 kind;
    u32 type_id;
    u32 child_start;
    u32 child_count;
    u32 extra;
    u32 token;
};

struct MethodSigInfo {
    u32 return_sig;
    u32 param_start;
    u32 param_count;
    u32 flags;
};

struct LocalSigInfo {
    u32 var_start;
    u32 var_count;
};

struct MemberRefInfo {
    u32 kind;      // 1 = method, 2 = field
    u32 decl_id;
    const char* name;
    u32 sig_index; // method sig for kind 1, field sig for kind 2
    u32 resolved_id;
    u32 flags;     // bit 0: resolved to an in-assembly def
};

struct MethodSpecInfo {
    u32 method_id;
    u32 arg_start;
    u32 arg_count;
    u32 memberref_id; // 1-based MemberRef row for external generic calls
};

struct FieldRvaInfo {
    u32 field_id;
    u32 data_offset;
    u32 size;
};

struct ParamInfo {
    const char* name;
    u32 flags;
};

struct ExceptionInfo {
    u32 flags;
    u32 try_offset;
    u32 try_length;
    u32 handler_offset;
    u32 handler_length;
    u32 catch_type;
};

struct UserStringHost {
    u32 token_offset;
    u32 char_offset;
    u32 length;
};

class MetadataPackage {
public:
    MetadataPackage();
    ~MetadataPackage();

    bool Load(const char* path);
    void Unload();
    bool IsLoaded() const { return file_ != 0; }

    u32 TypeCount() const { return type_count_; }
    u32 FieldCount() const { return field_count_; }
    u32 MethodCount() const { return method_count_; }
    u32 SigCount() const { return sig_count_; }
    u32 MemberRefCount() const { return memberref_count_; }
    u32 MethodSpecCount() const { return methodspec_count_; }
    u32 FieldRvaCount() const { return field_rva_count_; }
    const FieldRvaInfo& FieldRva(u32 index) const;
    const u8* FieldRvaData(u32 field_id, u32* out_size) const;

    const TypeInfo& Type(u32 id) const;
    const FieldInfo& Field(u32 id) const;
    const MethodInfo& Method(u32 id) const;
    const SigInfo& Sig(u32 index) const;
    const MethodSigInfo& MethodSig(u32 id) const;
    const LocalSigInfo& LocalSig(u32 id) const;
    const MemberRefInfo& MemberRef(u32 id) const;
    const MethodSpecInfo& MethodSpec(u32 id) const;
    const ParamInfo& Param(u32 id) const;
    const ExceptionInfo& Exception(u32 index) const;

    const u8* Il(u32 method_id) const;
    const char* StringAt(u32 offset) const;

    u32 InterfaceId(u32 index) const;
    u32 GenericArg(u32 index) const;
    u32 SigChild(u32 index) const;
    u32 MethodParamSig(u32 index) const;
    u32 LocalVarSig(u32 index) const;
    u32 MethodSpecArg(u32 index) const;

    // Token resolution (tokens use the CLI metadata token encoding).
    u32 ResolveTypeToken(u32 token) const;
    u32 ResolveMethodToken(u32 token) const;
    u32 ResolveFieldToken(u32 token) const;
    const MemberRefInfo* MemberRefForToken(u32 token) const;

    // Returns host-endian UTF-16 code units. token is the full 0x70xxxxxx
    // ldstr token.
    const u16* UserString(u32 token, u32* out_length) const;

    u32 FindType(const char* full_name) const;
    u32 FindMethod(u32 type_id, const char* name) const;
    u32 FindMethodBySig(u32 type_id, const char* name, u32 method_sig_id) const;
    u32 FindField(u32 type_id, const char* name) const;

    const char* LastError() const { return last_error_; }
    const char* FileBuffer() const { return (const char*)file_; }
    u32 FileSize() const { return file_size_; }

private:
    bool Fail(const char* message);
    void BuildTypeIndex();
    const u8* Section(u32 index, u32* size) const;

    u8* file_;
    u32 file_size_;
    const char* last_error_;

    u32 type_count_;
    u32 field_count_;
    u32 method_count_;
    u32 sig_count_;
    u32 method_sig_count_;
    u32 local_sig_count_;
    u32 user_string_count_;
    u32 memberref_count_;
    u32 methodspec_count_;
    u32 exception_count_;
    u32 param_count_;
    u32 field_rva_count_;

    const u8* strings_;
    u32 strings_size_;
    const u8* il_;
    u32 il_size_;
    const u8* user_bytes_;
    u32 user_bytes_size_;
    const u8* field_rva_bytes_;
    u32 field_rva_bytes_size_;

    Buffer<TypeInfo> types_;
    Buffer<FieldInfo> fields_;
    Buffer<MethodInfo> methods_;
    Buffer<SigInfo> sigs_;
    Buffer<MethodSigInfo> method_sigs_;
    Buffer<LocalSigInfo> local_sigs_;
    Buffer<MemberRefInfo> memberrefs_;
    Buffer<MethodSpecInfo> methodspecs_;
    Buffer<ParamInfo> params_;
    Buffer<ExceptionInfo> exceptions_;
    Buffer<FieldRvaInfo> field_rvas_;

    Buffer<u32> interface_ids_;
    Buffer<u32> generic_arg_ids_;
    Buffer<u32> sig_children_;
    Buffer<u32> method_param_sigs_;
    Buffer<u32> local_var_sigs_;
    Buffer<u32> methodspec_args_;

    Buffer<UserStringHost> user_strings_;
    Buffer<u16> user_chars_;

    Buffer<u32> type_buckets_;
    Buffer<u32> type_next_;
    u32 type_bucket_mask_;
};

} // namespace ch360

#endif // CH360_RUNTIME_METADATA_H
