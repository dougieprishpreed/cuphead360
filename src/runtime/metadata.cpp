#include "runtime/metadata.h"
#include "core/endian.h"
#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"

#include <stdlib.h>
#include <string.h>

namespace ch360 {

static const u32 kMagic = 0x43484D31u; // "CHM1"
static const u32 kVersion = 1;
static const u32 kHeaderWords = 80;
static const u32 kHeaderBytes = kHeaderWords * 4;
static const u32 kCountBase = 3;
static const u32 kOffsetBase = 24;
static const u32 kSizeBase = 46;

// Section indices in the package header.
enum SectionIndex {
    SEC_STRINGS = 0,
    SEC_TYPES,
    SEC_FIELDS,
    SEC_METHODS,
    SEC_SIGS,
    SEC_METHOD_SIGS,
    SEC_LOCAL_SIGS,
    SEC_IL,
    SEC_EXCEPTIONS,
    SEC_USER_STRINGS,
    SEC_USER_STRING_BYTES,
    SEC_MEMBERREFS,
    SEC_METHODSPECS,
    SEC_PARAMS,
    SEC_INTERFACE_IDS,
    SEC_GENERIC_ARG_IDS,
    SEC_SIG_CHILDREN,
    SEC_METHOD_PARAM_SIGS,
    SEC_LOCAL_VAR_SIGS,
    SEC_METHODSPEC_ARGS,
    SEC_FIELD_RVAS,
    SEC_FIELD_RVA_BYTES,
    SEC_COUNT
};

static u32 ReadWord(const u8* base, u32 index) {
    return ReadU32LE(base + index * 4);
}

static u32 HashName(const char* s) {
    u32 h = 2166136261u;
    while (s && *s) {
        h ^= (u8)*s++;
        h *= 16777619u;
    }
    return h;
}

static int CompareUserStrings(const void* a, const void* b) {
    const UserStringHost* x = (const UserStringHost*)a;
    const UserStringHost* y = (const UserStringHost*)b;
    if (x->token_offset < y->token_offset) return -1;
    if (x->token_offset > y->token_offset) return 1;
    return 0;
}

MetadataPackage::MetadataPackage()
    : file_(0), file_size_(0), last_error_("not loaded"), type_count_(0),
      field_count_(0), method_count_(0), sig_count_(0), method_sig_count_(0),
      local_sig_count_(0), user_string_count_(0), memberref_count_(0),
      methodspec_count_(0), exception_count_(0), param_count_(0),
      field_rva_count_(0), strings_(0), strings_size_(0), il_(0), il_size_(0),
      user_bytes_(0), user_bytes_size_(0), field_rva_bytes_(0),
      field_rva_bytes_size_(0), type_bucket_mask_(0) {}

MetadataPackage::~MetadataPackage() {
    Unload();
}

bool MetadataPackage::Fail(const char* message) {
    last_error_ = message;
    LogError("metadata", "%s", message);
    return false;
}

const u8* MetadataPackage::Section(u32 index, u32* size) const {
    if (!file_ || index >= SEC_COUNT) {
        if (size) *size = 0;
        return 0;
    }
    u32 off = ReadWord(file_, kOffsetBase + index);
    u32 sz = ReadWord(file_, kSizeBase + index);
    if (off > file_size_ || sz > file_size_ || off + sz > file_size_) {
        if (size) *size = 0;
        return 0;
    }
    if (size) *size = sz;
    return file_ + off;
}

const char* MetadataPackage::StringAt(u32 offset) const {
    if (!strings_ || offset >= strings_size_) return "";
    return (const char*)(strings_ + offset);
}

void MetadataPackage::Unload() {
    MemFree(file_);
    file_ = 0;
    file_size_ = 0;
    strings_ = 0;
    strings_size_ = 0;
    il_ = 0;
    il_size_ = 0;
    user_bytes_ = 0;
    user_bytes_size_ = 0;
    field_rva_bytes_ = 0;
    field_rva_bytes_size_ = 0;
    types_.Reset();
    fields_.Reset();
    methods_.Reset();
    sigs_.Reset();
    method_sigs_.Reset();
    local_sigs_.Reset();
    memberrefs_.Reset();
    methodspecs_.Reset();
    params_.Reset();
    exceptions_.Reset();
    interface_ids_.Reset();
    generic_arg_ids_.Reset();
    sig_children_.Reset();
    method_param_sigs_.Reset();
    local_var_sigs_.Reset();
    methodspec_args_.Reset();
    user_strings_.Reset();
    user_chars_.Reset();
    field_rvas_.Reset();
    type_buckets_.Reset();
    type_next_.Reset();
    type_count_ = field_count_ = method_count_ = sig_count_ = 0;
    method_sig_count_ = local_sig_count_ = user_string_count_ = 0;
    memberref_count_ = methodspec_count_ = exception_count_ = param_count_ = 0;
    field_rva_count_ = 0;
    type_bucket_mask_ = 0;
    last_error_ = "not loaded";
}

bool MetadataPackage::Load(const char* path) {
    Unload();
    file_ = FileReadAll(path, &file_size_);
    if (!file_) {
        last_error_ = "cannot read package";
        return false;
    }
    if (file_size_ < kHeaderBytes) return Fail("package too small");
    if (ReadWord(file_, 0) != kMagic) return Fail("bad package magic");
    if (ReadWord(file_, 1) != kVersion) return Fail("unsupported package version");
    if (ReadWord(file_, 2) != file_size_) return Fail("package size mismatch");

    u32 counts[18];
    for (u32 i = 0; i < 18; ++i) counts[i] = ReadWord(file_, kCountBase + i);
    type_count_ = counts[0];
    field_count_ = counts[1];
    method_count_ = counts[2];
    sig_count_ = counts[3];
    method_sig_count_ = counts[4];
    local_sig_count_ = counts[5];
    user_string_count_ = counts[6];
    memberref_count_ = counts[7];
    methodspec_count_ = counts[8];
    exception_count_ = counts[9];
    param_count_ = counts[10];
    field_rva_count_ = counts[17];

    u32 sec_size = 0;
    strings_ = Section(SEC_STRINGS, &strings_size_);
    il_ = Section(SEC_IL, &il_size_);
    user_bytes_ = Section(SEC_USER_STRING_BYTES, &user_bytes_size_);
    field_rva_bytes_ = Section(SEC_FIELD_RVA_BYTES, &field_rva_bytes_size_);
    if (!strings_ || !il_ || !user_bytes_) return Fail("missing core section");

    // Fixed-size records.
    const u8* type_data = Section(SEC_TYPES, &sec_size);
    if (!type_data || sec_size < type_count_ * 128u) return Fail("bad type section");
    if (!types_.Resize(type_count_)) return Fail("out of memory for types");
    for (u32 i = 0; i < type_count_; ++i) {
        const u8* p = type_data + i * 128u;
        TypeInfo& t = types_[i];
        t.name = StringAt(ReadU32LE(p + 0));
        t.ns = StringAt(ReadU32LE(p + 4));
        t.full_name = StringAt(ReadU32LE(p + 8));
        t.assembly = StringAt(ReadU32LE(p + 12));
        t.flags = ReadU32LE(p + 16);
        t.base_id = ReadU32LE(p + 20);
        t.decl_id = ReadU32LE(p + 24);
        t.field_start = ReadU32LE(p + 28);
        t.field_count = ReadU32LE(p + 32);
        t.method_start = ReadU32LE(p + 36);
        t.method_count = ReadU32LE(p + 40);
        t.interface_start = ReadU32LE(p + 44);
        t.interface_count = ReadU32LE(p + 48);
        t.size = ReadU32LE(p + 52);
        t.static_size = ReadU32LE(p + 56);
        t.vtable_size = ReadU32LE(p + 60);
        t.generic_type_id = ReadU32LE(p + 64);
        t.generic_arg_start = ReadU32LE(p + 68);
        t.generic_arg_count = ReadU32LE(p + 72);
        t.element_id = ReadU32LE(p + 76);
        t.external = ReadU32LE(p + 80);
        t.composite = ReadU32LE(p + 84);
        t.is_value_type = ReadU32LE(p + 88);
        t.is_enum = ReadU32LE(p + 92);
        t.is_interface = ReadU32LE(p + 96);
        t.is_abstract = ReadU32LE(p + 100);
        t.is_sealed = ReadU32LE(p + 104);
        t.kind = ReadU32LE(p + 108);
        t.class_size = ReadU32LE(p + 112);
        t.pack = ReadU32LE(p + 116);
        t.generic_arity = ReadU32LE(p + 120);
    }

    const u8* field_data = Section(SEC_FIELDS, &sec_size);
    if (!field_data || sec_size < field_count_ * 24u) return Fail("bad field section");
    if (!fields_.Resize(field_count_)) return Fail("out of memory for fields");
    for (u32 i = 0; i < field_count_; ++i) {
        const u8* p = field_data + i * 24u;
        FieldInfo& f = fields_[i];
        f.name = StringAt(ReadU32LE(p + 0));
        f.type_sig = ReadU32LE(p + 4);
        f.flags = ReadU32LE(p + 8);
        f.offset = ReadU32LE(p + 12);
        f.decl_id = ReadU32LE(p + 16);
    }

    const u8* method_data = Section(SEC_METHODS, &sec_size);
    if (!method_data || sec_size < method_count_ * 64u) return Fail("bad method section");
    if (!methods_.Resize(method_count_)) return Fail("out of memory for methods");
    for (u32 i = 0; i < method_count_; ++i) {
        const u8* p = method_data + i * 64u;
        MethodInfo& m = methods_[i];
        m.name = StringAt(ReadU32LE(p + 0));
        m.sig = ReadU32LE(p + 4);
        m.flags = ReadU32LE(p + 8);
        m.impl_flags = ReadU32LE(p + 12);
        m.il_offset = ReadU32LE(p + 16);
        m.il_size = ReadU32LE(p + 20);
        m.local_sig = ReadU32LE(p + 24);
        m.exception_start = ReadU32LE(p + 28);
        m.exception_count = ReadU32LE(p + 32);
        m.vtable_slot = ReadU32LE(p + 36);
        m.decl_id = ReadU32LE(p + 40);
        m.param_start = ReadU32LE(p + 44);
        m.param_count = ReadU32LE(p + 48);
        m.generic_params = ReadU32LE(p + 52);
    }

    const u8* sig_data = Section(SEC_SIGS, &sec_size);
    if (!sig_data || sec_size < sig_count_ * 24u) return Fail("bad signature section");
    if (!sigs_.Resize(sig_count_)) return Fail("out of memory for signatures");
    for (u32 i = 0; i < sig_count_; ++i) {
        const u8* p = sig_data + i * 24u;
        SigInfo& s = sigs_[i];
        s.kind = ReadU32LE(p + 0);
        s.type_id = ReadU32LE(p + 4);
        s.child_start = ReadU32LE(p + 8);
        s.child_count = ReadU32LE(p + 12);
        s.extra = ReadU32LE(p + 16);
        s.token = ReadU32LE(p + 20);
    }

    const u8* ms_data = Section(SEC_METHOD_SIGS, &sec_size);
    if (!ms_data || sec_size < method_sig_count_ * 16u) return Fail("bad method sig section");
    if (!method_sigs_.Resize(method_sig_count_)) return Fail("out of memory for method sigs");
    for (u32 i = 0; i < method_sig_count_; ++i) {
        const u8* p = ms_data + i * 16u;
        MethodSigInfo& s = method_sigs_[i];
        s.return_sig = ReadU32LE(p + 0);
        s.param_start = ReadU32LE(p + 4);
        s.param_count = ReadU32LE(p + 8);
        s.flags = ReadU32LE(p + 12);
    }

    const u8* ls_data = Section(SEC_LOCAL_SIGS, &sec_size);
    if (!ls_data || sec_size < local_sig_count_ * 8u) return Fail("bad local sig section");
    if (!local_sigs_.Resize(local_sig_count_)) return Fail("out of memory for local sigs");
    for (u32 i = 0; i < local_sig_count_; ++i) {
        const u8* p = ls_data + i * 8u;
        LocalSigInfo& s = local_sigs_[i];
        s.var_start = ReadU32LE(p + 0);
        s.var_count = ReadU32LE(p + 4);
    }

    const u8* mr_data = Section(SEC_MEMBERREFS, &sec_size);
    if (!mr_data || sec_size < memberref_count_ * 24u) return Fail("bad memberref section");
    if (!memberrefs_.Resize(memberref_count_)) return Fail("out of memory for memberrefs");
    for (u32 i = 0; i < memberref_count_; ++i) {
        const u8* p = mr_data + i * 24u;
        MemberRefInfo& r = memberrefs_[i];
        r.kind = ReadU32LE(p + 0);
        r.decl_id = ReadU32LE(p + 4);
        r.name = StringAt(ReadU32LE(p + 8));
        r.sig_index = ReadU32LE(p + 12);
        r.resolved_id = ReadU32LE(p + 16);
        r.flags = ReadU32LE(p + 20);
    }

    const u8* mspec_data = Section(SEC_METHODSPECS, &sec_size);
    if (!mspec_data || sec_size < methodspec_count_ * 16u) return Fail("bad methodspec section");
    if (!methodspecs_.Resize(methodspec_count_)) return Fail("out of memory for methodspecs");
    for (u32 i = 0; i < methodspec_count_; ++i) {
        const u8* p = mspec_data + i * 16u;
        MethodSpecInfo& s = methodspecs_[i];
        s.method_id = ReadU32LE(p + 0);
        s.arg_start = ReadU32LE(p + 4);
        s.arg_count = ReadU32LE(p + 8);
        s.memberref_id = ReadU32LE(p + 12);
    }

    const u8* param_data = Section(SEC_PARAMS, &sec_size);
    if (!param_data || sec_size < param_count_ * 8u) return Fail("bad param section");
    if (!params_.Resize(param_count_)) return Fail("out of memory for params");
    for (u32 i = 0; i < param_count_; ++i) {
        const u8* p = param_data + i * 8u;
        ParamInfo& s = params_[i];
        s.name = StringAt(ReadU32LE(p + 0));
        s.flags = ReadU32LE(p + 4);
    }

    const u8* rva_data = Section(SEC_FIELD_RVAS, &sec_size);
    if (field_rva_count_) {
        if (!rva_data || !field_rva_bytes_ || sec_size < field_rva_count_ * 12u) {
            return Fail("bad field RVA section");
        }
        if (!field_rvas_.Resize(field_rva_count_)) return Fail("out of memory for field RVAs");
        for (u32 i = 0; i < field_rva_count_; ++i) {
            const u8* p = rva_data + i * 12u;
            FieldRvaInfo& r = field_rvas_[i];
            r.field_id = ReadU32LE(p + 0);
            r.data_offset = ReadU32LE(p + 4);
            r.size = ReadU32LE(p + 8);
            if (r.data_offset > field_rva_bytes_size_ ||
                r.size > field_rva_bytes_size_ - r.data_offset) {
                return Fail("field RVA data out of bounds");
            }
        }
    }

    const u8* exc_data = Section(SEC_EXCEPTIONS, &sec_size);
    if (!exc_data || sec_size < exception_count_ * 24u) return Fail("bad exception section");
    if (!exceptions_.Resize(exception_count_)) return Fail("out of memory for exceptions");
    for (u32 i = 0; i < exception_count_; ++i) {
        const u8* p = exc_data + i * 24u;
        ExceptionInfo& e = exceptions_[i];
        e.flags = ReadU32LE(p + 0);
        e.try_offset = ReadU32LE(p + 4);
        e.try_length = ReadU32LE(p + 8);
        e.handler_offset = ReadU32LE(p + 12);
        e.handler_length = ReadU32LE(p + 16);
        e.catch_type = ReadU32LE(p + 20);
    }

    // Auxiliary arrays. Their counts are words 14..19.
    struct Aux {
        u32 section;
        u32 count;
        Buffer<u32>* dest;
    };
    Aux aux[6] = {
        { SEC_INTERFACE_IDS, counts[11], &interface_ids_ },
        { SEC_GENERIC_ARG_IDS, counts[12], &generic_arg_ids_ },
        { SEC_SIG_CHILDREN, counts[13], &sig_children_ },
        { SEC_METHOD_PARAM_SIGS, counts[14], &method_param_sigs_ },
        { SEC_LOCAL_VAR_SIGS, counts[15], &local_var_sigs_ },
        { SEC_METHODSPEC_ARGS, counts[16], &methodspec_args_ },
    };
    for (u32 a = 0; a < 6; ++a) {
        u32 n = aux[a].count;
        const u8* p = Section(aux[a].section, &sec_size);
        if (!p || sec_size < n * 4u) return Fail("bad auxiliary section");
        if (!aux[a].dest->Resize(n)) return Fail("out of memory for auxiliary data");
        for (u32 i = 0; i < n; ++i) {
            (*aux[a].dest)[i] = ReadU32LE(p + i * 4u);
        }
    }

    // User strings.
    const u8* us_data = Section(SEC_USER_STRINGS, &sec_size);
    if (!us_data || sec_size < user_string_count_ * 12u) return Fail("bad user string section");
    if (!user_strings_.Resize(user_string_count_)) return Fail("out of memory for user strings");
    u32 total_chars = 0;
    for (u32 i = 0; i < user_string_count_; ++i) {
        const u8* p = us_data + i * 12u;
        UserStringHost& s = user_strings_[i];
        s.token_offset = ReadU32LE(p + 0);
        s.char_offset = total_chars;
        s.length = ReadU32LE(p + 8);
        total_chars += s.length;
    }
    if (!user_chars_.Resize(total_chars)) return Fail("out of memory for user string data");
    for (u32 i = 0; i < user_string_count_; ++i) {
        UserStringHost& s = user_strings_[i];
        u32 data_off = ReadU32LE(us_data + i * 12u + 4);
        if (data_off + s.length * 2u > user_bytes_size_) return Fail("bad user string data");
        for (u32 j = 0; j < s.length; ++j) {
            user_chars_[s.char_offset + j] = ReadU16LE(user_bytes_ + data_off + j * 2u);
        }
    }
    qsort(user_strings_.Data(), user_string_count_, sizeof(UserStringHost), CompareUserStrings);

    BuildTypeIndex();
    if (type_buckets_.Empty()) return Fail("out of memory for type index");
    last_error_ = "ok";
    LogInfo("metadata", "loaded %s types=%u fields=%u methods=%u sigs=%u memberrefs=%u user_strings=%u",
            path, type_count_, field_count_, method_count_, sig_count_,
            memberref_count_, user_string_count_);
    return true;
}

void MetadataPackage::BuildTypeIndex() {
    const u32 bucket_count = 16384;
    if (!type_buckets_.Resize(bucket_count)) return;
    for (u32 i = 0; i < bucket_count; ++i) type_buckets_[i] = 0xFFFFFFFFu;
    if (!type_next_.Resize(type_count_)) return;
    type_bucket_mask_ = bucket_count - 1u;
    for (u32 i = 0; i < type_count_; ++i) {
        u32 h = HashName(types_[i].full_name) & type_bucket_mask_;
        type_next_[i] = type_buckets_[h];
        type_buckets_[h] = i;
    }
}

const TypeInfo& MetadataPackage::Type(u32 id) const {
    static TypeInfo empty;
    if (id == 0 || id > type_count_) return empty;
    return types_[id - 1];
}

const FieldInfo& MetadataPackage::Field(u32 id) const {
    static FieldInfo empty;
    if (id == 0 || id > field_count_) return empty;
    return fields_[id - 1];
}

const MethodInfo& MetadataPackage::Method(u32 id) const {
    static MethodInfo empty;
    if (id == 0 || id > method_count_) return empty;
    return methods_[id - 1];
}

const SigInfo& MetadataPackage::Sig(u32 index) const {
    static SigInfo empty;
    if (index >= sig_count_) return empty;
    return sigs_[index];
}

const MethodSigInfo& MetadataPackage::MethodSig(u32 id) const {
    static MethodSigInfo empty;
    if (id == 0 || id > method_sig_count_) return empty;
    return method_sigs_[id - 1];
}

const LocalSigInfo& MetadataPackage::LocalSig(u32 id) const {
    static LocalSigInfo empty;
    if (id == 0 || id > local_sig_count_) return empty;
    return local_sigs_[id - 1];
}

const MemberRefInfo& MetadataPackage::MemberRef(u32 id) const {
    static MemberRefInfo empty;
    if (id == 0 || id > memberref_count_) return empty;
    return memberrefs_[id - 1];
}

const MethodSpecInfo& MetadataPackage::MethodSpec(u32 id) const {
    static MethodSpecInfo empty;
    if (id == 0 || id > methodspec_count_) return empty;
    return methodspecs_[id - 1];
}

const ParamInfo& MetadataPackage::Param(u32 id) const {
    static ParamInfo empty;
    if (id == 0 || id > param_count_) return empty;
    return params_[id - 1];
}

const FieldRvaInfo& MetadataPackage::FieldRva(u32 index) const {
    static FieldRvaInfo empty = { 0, 0, 0 };
    if (index == 0 || index > field_rva_count_) return empty;
    return field_rvas_[index - 1];
}

const u8* MetadataPackage::FieldRvaData(u32 field_id, u32* out_size) const {
    if (out_size) *out_size = 0;
    if (!field_rva_bytes_) return 0;
    for (u32 i = 0; i < field_rva_count_; ++i) {
        const FieldRvaInfo& r = field_rvas_[i];
        if (r.field_id == field_id) {
            if (out_size) *out_size = r.size;
            return field_rva_bytes_ + r.data_offset;
        }
    }
    return 0;
}

const ExceptionInfo& MetadataPackage::Exception(u32 index) const {
    static ExceptionInfo empty;
    if (index >= exception_count_) return empty;
    return exceptions_[index];
}

const u8* MetadataPackage::Il(u32 method_id) const {
    if (method_id == 0 || method_id > method_count_) return 0;
    const MethodInfo& m = methods_[method_id - 1];
    if (m.il_size == 0 || m.il_offset > il_size_ || m.il_offset + m.il_size > il_size_) return 0;
    return il_ + m.il_offset;
}

u32 MetadataPackage::InterfaceId(u32 index) const {
    return index < interface_ids_.Size() ? interface_ids_[index] : 0;
}
u32 MetadataPackage::GenericArg(u32 index) const {
    return index < generic_arg_ids_.Size() ? generic_arg_ids_[index] : 0;
}
u32 MetadataPackage::SigChild(u32 index) const {
    return index < sig_children_.Size() ? sig_children_[index] : 0;
}
u32 MetadataPackage::MethodParamSig(u32 index) const {
    return index < method_param_sigs_.Size() ? method_param_sigs_[index] : 0;
}
u32 MetadataPackage::LocalVarSig(u32 index) const {
    return index < local_var_sigs_.Size() ? local_var_sigs_[index] : 0;
}
u32 MetadataPackage::MethodSpecArg(u32 index) const {
    return index < methodspec_args_.Size() ? methodspec_args_[index] : 0;
}

u32 MetadataPackage::ResolveTypeToken(u32 token) const {
    u32 table = token >> 24;
    if (table == 0x02) {
        u32 id = token & 0xFFFFFF;
        return id <= type_count_ ? id : 0;
    }
    return 0;
}

u32 MetadataPackage::ResolveMethodToken(u32 token) const {
    u32 table = token >> 24;
    u32 index = token & 0xFFFFFF;
    if (table == 0x06) {
        return index <= method_count_ ? index : 0;
    }
    if (table == 0x0A) {
        if (index == 0 || index > memberref_count_) return 0;
        const MemberRefInfo& r = memberrefs_[index - 1];
        if (r.kind == 1 && r.resolved_id) return r.resolved_id;
        return 0;
    }
    if (table == 0x2B) {
        if (index == 0 || index > methodspec_count_) return 0;
        return methodspecs_[index - 1].method_id;
    }
    return 0;
}

u32 MetadataPackage::ResolveFieldToken(u32 token) const {
    u32 table = token >> 24;
    u32 index = token & 0xFFFFFF;
    if (table == 0x04) {
        return index <= field_count_ ? index : 0;
    }
    if (table == 0x0A) {
        if (index == 0 || index > memberref_count_) return 0;
        const MemberRefInfo& r = memberrefs_[index - 1];
        if (r.kind == 2 && r.resolved_id) return r.resolved_id;
        return 0;
    }
    return 0;
}

const MemberRefInfo* MetadataPackage::MemberRefForToken(u32 token) const {
    if ((token >> 24) != 0x0A) return 0;
    u32 index = token & 0xFFFFFF;
    if (index == 0 || index > memberref_count_) return 0;
    return &memberrefs_[index - 1];
}

const u16* MetadataPackage::UserString(u32 token, u32* out_length) const {
    if (out_length) *out_length = 0;
    if ((token >> 24) != 0x70) return 0;
    u32 target = token & 0xFFFFFF;
    u32 lo = 0, hi = user_string_count_;
    while (lo < hi) {
        u32 mid = (lo + hi) / 2;
        if (user_strings_[mid].token_offset < target) lo = mid + 1;
        else hi = mid;
    }
    if (lo >= user_string_count_ || user_strings_[lo].token_offset != target) return 0;
    if (out_length) *out_length = user_strings_[lo].length;
    return user_chars_.Data() + user_strings_[lo].char_offset;
}

u32 MetadataPackage::FindType(const char* full_name) const {
    if (!full_name || !type_buckets_.Size()) return 0;
    u32 h = HashName(full_name) & type_bucket_mask_;
    u32 i = type_buckets_[h];
    while (i != 0xFFFFFFFFu) {
        if (strcmp(types_[i].full_name, full_name) == 0) return i + 1;
        i = type_next_[i];
    }
    return 0;
}

u32 MetadataPackage::FindMethod(u32 type_id, const char* name) const {
    if (type_id == 0 || type_id > type_count_ || !name) return 0;
    const TypeInfo& t = types_[type_id - 1];
    for (u32 id = t.method_start; id < t.method_start + t.method_count; ++id) {
        const MethodInfo& m = Method(id);
        if (strcmp(m.name, name) == 0) return id;
    }
    return 0;
}

u32 MetadataPackage::FindMethodBySig(u32 type_id, const char* name, u32 method_sig_id) const {
    if (type_id == 0 || type_id > type_count_ || !name) return 0;
    const TypeInfo& t = types_[type_id - 1];
    for (u32 id = t.method_start; id < t.method_start + t.method_count; ++id) {
        const MethodInfo& m = Method(id);
        if (strcmp(m.name, name) == 0 && m.sig == method_sig_id) return id;
    }
    return 0;
}

u32 MetadataPackage::FindField(u32 type_id, const char* name) const {
    if (type_id == 0 || type_id > type_count_ || !name) return 0;
    const TypeInfo& t = types_[type_id - 1];
    for (u32 id = t.field_start; id < t.field_start + t.field_count; ++id) {
        const FieldInfo& f = Field(id);
        if (strcmp(f.name, name) == 0) return id;
    }
    return 0;
}

} // namespace ch360
