# Cross-architecture RyuJIT disassembly capture for LightEpoch.
#
# The dumps in ../jit/ have to exist for both x86-64 and AArch64, but the obvious
# way to get the AArch64 ones is to own an AArch64 machine. NativeAOT's compiler
# hosts the same RyuJIT and cross-targets, so a single x64 container can emit both.
#
# The image carries only the toolchain. The repository is mounted at /repo so the
# script can pull LightEpoch.cs out of any git ref without the image having to be
# rebuilt when the source changes.
#
# Build (from the repository root):
#   docker build -t garnet-lightepoch-disasm libs/storage/Tsavorite/cs/test/epoch/formal-verification/herd7/capture
#
# Run (writes <label>-<arch>.asm into the mounted output directory):
#   docker run --rm -v "$PWD:/repo:ro" -v "$PWD/out:/out" garnet-lightepoch-disasm
#
# By default it captures two labels, "fixed" from HEAD and "main" from origin/main.
# Override by passing label=gitref pairs:
#   docker run --rm -v ... garnet-lightepoch-disasm fixed=HEAD main=origin/main

FROM mcr.microsoft.com/dotnet/sdk:10.0

# clang + lld link the NativeAOT object file; the aarch64 binutils and the arm64
# multiarch libc/zlib are what let the linux-arm64 publish complete on an x64 host.
#
# Ubuntu serves arm64 from ports.ubuntu.com rather than archive.ubuntu.com, so the
# stock sources have to be pinned to amd64 and a separate arm64 source added, or
# `apt-get update` 404s on every arm64 index.
RUN set -eux; \
    . /etc/os-release; \
    if [ -f /etc/apt/sources.list.d/ubuntu.sources ]; then \
        sed -i 's|^Types: deb$|Types: deb\nArchitectures: amd64|' /etc/apt/sources.list.d/ubuntu.sources; \
        printf 'Types: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports/\nSuites: %s %s-updates %s-security\nComponents: main universe\nArchitectures: arm64\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n' \
            "$VERSION_CODENAME" "$VERSION_CODENAME" "$VERSION_CODENAME" \
            > /etc/apt/sources.list.d/arm64.sources; \
    fi; \
    dpkg --add-architecture arm64; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        git clang lld llvm binutils-aarch64-linux-gnu gcc-aarch64-linux-gnu \
        libc6-dev:arm64 zlib1g-dev:arm64; \
    rm -rf /var/lib/apt/lists/*

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/nuget

WORKDIR /work
COPY . .
RUN sed -i 's/\r$//' capture.sh && chmod +x capture.sh

# Warm the NuGet cache with both ILCompiler packages so a capture run does not
# have to reach the network. Restore does not compile, so no source is needed yet.
RUN for rid in linux-x64 linux-arm64; do \
      dotnet restore Disasm.csproj -r $rid -p:PublishAot=true; \
    done

ENTRYPOINT ["bash", "capture.sh"]
