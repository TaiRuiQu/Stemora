#pragma once
#include <stddef.h>
#include <stdint.h>
#if defined(_WIN32)
#if defined(STEMORA_INFRA_BUILD)
#define STEMORA_API __declspec(dllexport)
#else
#define STEMORA_API __declspec(dllimport)
#endif
#else
#define STEMORA_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* Stable C ABI. PCM is interleaved float32 stereo. Strings are UTF-8.
   One processing call per session; cancel is safe from another thread.
   Returned JSON remains valid until the owning session is destroyed. */
typedef struct stemora_session stemora_session;
typedef void (*stemora_progress_callback)(float fraction, const char *stage, void *user);
typedef struct stemora_options {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t device;       /* 0 automatic (CUDA then CPU), 1 CPU, 2 CUDA (strict) */
    int32_t threads;      /* 0 automatic */
    int32_t chunk_frames; /* 0 model default */
    int32_t overlap;      /* 0 model default */
    const char *profile;  /* optional verified model profile, e.g. bs_roformer_sw */
    const char *stems;    /* optional comma-separated output IDs, in model order */
} stemora_options;

enum { STEMORA_OK = 0, STEMORA_ERROR = -1, STEMORA_CANCELLED = -2, STEMORA_INVALID_ARGUMENT = -3 };
STEMORA_API uint32_t stemora_abi_version(void);
STEMORA_API const char *stemora_version(void);
STEMORA_API const char *stemora_last_error(void);
/* Probe does not allocate model weights or initialize a GPU. Returns required
   UTF-8 bytes including the terminator; 0 indicates an error. */
STEMORA_API size_t stemora_probe(const char *model, const stemora_options *options, char *json, size_t capacity);
STEMORA_API int stemora_create(const char *model, const stemora_options *options, stemora_session **session);
STEMORA_API const char *stemora_model_json(const stemora_session *session);
STEMORA_API int stemora_process(stemora_session *session, const float *pcm, uint64_t frames,
                                stemora_progress_callback callback, void *user);
STEMORA_API void stemora_cancel(stemora_session *session);
STEMORA_API uint32_t stemora_stem_count(const stemora_session *session);
STEMORA_API const char *stemora_stem_name(const stemora_session *session, uint32_t index);
STEMORA_API uint64_t stemora_frame_count(const stemora_session *session);
STEMORA_API int stemora_copy_stem(const stemora_session *session, uint32_t index, float *pcm, uint64_t sample_capacity);
STEMORA_API void stemora_destroy(stemora_session *session);

#ifdef __cplusplus
}
#endif
