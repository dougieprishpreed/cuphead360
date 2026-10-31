#ifndef CH360_CORE_CONTAINERS_H
#define CH360_CORE_CONTAINERS_H

#include "core/types.h"
#include "core/mem.h"

#include <string.h>
#include <stdarg.h>
#include <stdio.h>

namespace ch360 {

// A simple dynamic array for POD types. It intentionally does not run C++
// constructors/destructors; use it for value records, handles, and pointers.
template <typename T>
class Buffer {
public:
    Buffer() : data_(0), size_(0), capacity_(0) {}
    ~Buffer() { MemFree(data_); }

    T* Data() { return data_; }
    const T* Data() const { return data_; }
    u32 Size() const { return size_; }
    u32 Capacity() const { return capacity_; }
    bool Empty() const { return size_ == 0; }

    T& operator[](u32 i) { return data_[i]; }
    const T& operator[](u32 i) const { return data_[i]; }

    void Clear() { size_ = 0; }
    void Reset() { MemFree(data_); data_ = 0; size_ = 0; capacity_ = 0; }

    bool Reserve(u32 n) {
        if (n <= capacity_) return true;
        u32 cap = capacity_ ? capacity_ : 8;
        while (cap < n) {
            if (cap > 0x7FFFFFFFu) return false;
            cap *= 2;
        }
        T* fresh = (T*)MemAlloc(cap * (u32)sizeof(T));
        if (!fresh) return false;
        if (size_) memcpy(fresh, data_, size_ * (u32)sizeof(T));
        MemFree(data_);
        data_ = fresh;
        capacity_ = cap;
        return true;
    }

    bool PushBack(const T& value) {
        if (!Reserve(size_ + 1)) return false;
        data_[size_++] = value;
        return true;
    }

    bool Resize(u32 n) {
        if (!Reserve(n)) return false;
        if (n > size_) memset(data_ + size_, 0, (n - size_) * sizeof(T));
        size_ = n;
        return true;
    }

    bool Insert(u32 index, const T& value) {
        if (index > size_) return false;
        if (!Reserve(size_ + 1)) return false;
        if (index < size_) {
            memmove(data_ + index + 1, data_ + index, (size_ - index) * sizeof(T));
        }
        data_[index] = value;
        ++size_;
        return true;
    }

    bool RemoveAt(u32 index) {
        if (index >= size_) return false;
        if (index + 1 < size_) {
            memmove(data_ + index, data_ + index + 1, (size_ - index - 1) * sizeof(T));
        }
        --size_;
        return true;
    }

    void PopBack() { if (size_) --size_; }

    T* begin() { return data_; }
    T* end() { return data_ + size_; }
    const T* begin() const { return data_; }
    const T* end() const { return data_ + size_; }

private:
    Buffer(const Buffer&);
    Buffer& operator=(const Buffer&);

    T* data_;
    u32 size_;
    u32 capacity_;
};

// A growable byte/character buffer with printf helpers.
class StringBuf {
public:
    StringBuf() : data_(0), size_(0), capacity_(0) {}
    ~StringBuf() { MemFree(data_); }

    const char* CStr() const { return data_ ? data_ : ""; }
    u32 Size() const { return size_; }
    bool Empty() const { return size_ == 0; }
    void Clear() { size_ = 0; if (data_) data_[0] = 0; }

    bool Reserve(u32 n) {
        u32 need = n + 1;
        if (need <= capacity_) return true;
        u32 cap = capacity_ ? capacity_ : 64;
        while (cap < need) cap *= 2;
        char* fresh = (char*)MemAlloc(cap);
        if (!fresh) return false;
        if (size_) memcpy(fresh, data_, size_);
        MemFree(data_);
        data_ = fresh;
        capacity_ = cap;
        data_[size_] = 0;
        return true;
    }

    bool Append(const char* s) {
        if (!s) return true;
        return AppendN(s, (u32)strlen(s));
    }

    bool AppendN(const char* s, u32 n) {
        if (!Reserve(size_ + n)) return false;
        memcpy(data_ + size_, s, n);
        size_ += n;
        data_[size_] = 0;
        return true;
    }

    bool AppendChar(char c) {
        if (!Reserve(size_ + 1)) return false;
        data_[size_++] = c;
        data_[size_] = 0;
        return true;
    }

    bool AppendF(const char* fmt, ...) {
        char tmp[1024];
        va_list ap;
        va_start(ap, fmt);
        _vsnprintf(tmp, sizeof(tmp) - 1, fmt, ap);
        va_end(ap);
        tmp[sizeof(tmp) - 1] = 0;
        return Append(tmp);
    }

    // Returns a newly allocated copy.
    char* Copy() const {
        char* out = (char*)MemAlloc(size_ + 1);
        if (!out) return 0;
        if (size_) memcpy(out, data_, size_);
        out[size_] = 0;
        return out;
    }

private:
    StringBuf(const StringBuf&);
    StringBuf& operator=(const StringBuf&);

    char* data_;
    u32 size_;
    u32 capacity_;
};

} // namespace ch360

#endif // CH360_CORE_CONTAINERS_H
