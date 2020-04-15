
#include "sdk.h"

libuv_ctx* new_libuv_ctx() {
    return calloc(1, sizeof(libuv_ctx));
}
void libuv_stopper(uv_async_t *a) {
     uv_stop(a->loop);
}
void libuv_init(libuv_ctx *lctx) {
    lctx->l = uv_default_loop();
    uv_async_init(lctx->l, &lctx->stopper, libuv_stopper);
}
void libuv_runner(void *arg) {
    uv_loop_t *l = arg;
    uv_run(l, UV_RUN_DEFAULT);
}

void libuv_run(libuv_ctx *lctx) {
    uv_thread_create(&lctx->t, libuv_runner, lctx->l);
}
void libuv_stop(libuv_ctx *lctx) {
    uv_async_send(&lctx->stopper);
    int err = uv_thread_join(&lctx->t);
    if (err) {
        fprintf(stderr, "%d: %s\n", err, uv_strerror(err));
    }
}

extern void initCB(nf_context nf, int status, void *ctx);
extern void _init_cb(nf_context nf, int status, void *ctx) {
initCB(nf, status, ctx);
}

static const char* _all[] = {
   "all", NULL
};

const char** all_configs = _all;