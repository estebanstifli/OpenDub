# Same audio.cpp revision as runtimes/versions.json; build both server and separation CLI.
FROM nvidia/cuda:12.8.1-devel-ubuntu24.04@sha256:520292dbb4f755fd360766059e62956e9379485d9e073bbd2f6e3c20c270ed66 AS build
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates curl cmake ninja-build g++ libssl-dev && rm -rf /var/lib/apt/lists/*
ARG AUDIO_REVISION=795c45fbde0a7d29c93b22199728ff5caaec02e5
RUN mkdir /src && curl -fsSL https://codeload.github.com/0xShug0/audio.cpp/tar.gz/${AUDIO_REVISION} | tar xz -C /src --strip-components=1
WORKDIR /src
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    '-DCMAKE_CUDA_ARCHITECTURES=75;80;86;89;90' \
    '-DCMAKE_EXE_LINKER_FLAGS=-static-libstdc++ -static-libgcc' \
    -DENGINE_ENABLE_CUDA=ON -DENGINE_ENABLE_VULKAN=OFF -DENGINE_ENABLE_NATIVE_CPU=OFF \
    -DENGINE_BUILD_TESTS=OFF -DAUDIOCPP_VERSION=0.9.0 -DAUDIOCPP_DEPLOYMENT_BUILD=ON \
    -DAUDIOCPP_BUILD_NATIVE_MODEL_MANAGER=ON -DAUDIOCPP_USE_SYSTEM_OPENSSL=ON \
    -DENGINE_ENABLE_LLAMAFILE=ON -DENGINE_ENABLE_CUDA_GRAPHS=ON -DGGML_CUDA_NCCL=OFF \
    && cmake --build build --parallel 4 --target audiocpp_server audiocpp_cli
RUN mkdir -p /out/licenses && cp build/bin/audiocpp_server build/bin/audiocpp_cli /out/ \
    && cp LICENSE /out/licenses/audio.cpp-LICENSE && cp -r model_specs /out/ \
    && cp -L /usr/local/cuda/targets/x86_64-linux/lib/libcudart.so.12 /usr/local/cuda/targets/x86_64-linux/lib/libcublas.so.12 /usr/local/cuda/targets/x86_64-linux/lib/libcublasLt.so.12 /usr/local/cuda/targets/x86_64-linux/lib/libcufft.so.11 /out/ \
    && cp /usr/local/cuda/doc/EULA.txt /out/licenses/NVIDIA-CUDA-EULA.txt \
    && echo "audio.cpp ${AUDIO_REVISION}; CUDA 12.8.1; SM 75,80,86,89,90; unmodified upstream sources; built by scripts/audio-linux-cuda.Dockerfile" > /out/BUILD-SOURCES.txt
FROM scratch
COPY --from=build /out/ /
