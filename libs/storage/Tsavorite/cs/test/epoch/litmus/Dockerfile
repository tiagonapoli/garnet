# Store-Buffer quarantine litmus for LightEpoch, as a container.
#
# The build context is the repository root, because the tool builds against the real
# Garnet.LightEpoch project rather than a copy:
#
#   docker build -f libs/storage/Tsavorite/cs/test/epoch/litmus/Dockerfile -t garnet-lightepoch-litmus .
#
# Run the default check (forced-failure control, then a 30 s soak):
#
#   docker run --rm garnet-lightepoch-litmus
#
# Longer soak with a machine-readable summary on stdout:
#
#   docker run --rm garnet-lightepoch-litmus --seconds 3600 --iterations 8 --json -
#
# The container needs at least 4 logical processors, and the harness pins threads with
# sched_setaffinity, so do not constrain it with --cpuset-cpus to fewer cores than that.
#
# READ THIS BEFORE RUNNING IT FOR ANOTHER ARCHITECTURE. Building this image with
# --platform linux/arm64 on an x86 host produces a binary that runs under QEMU, and an
# emulator does not reproduce the emulated architecture's memory ordering: the reorderings
# this harness exists to catch simply cannot occur, so the run comes back clean no matter
# what the code does. The forced-failure control does not protect you here, because it
# recycles pages unconditionally rather than relying on a reordering, so it fires under
# emulation exactly as it does on real hardware. The tool detects the common case and
# refuses to report a pass, but detection is heuristic. For an architecture other than the
# host's, build and run this on a native machine of that architecture.

ARG DOTNET_SDK_TAG=10.0
ARG DOTNET_RUNTIME_TAG=8.0
ARG TFM=net8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build
ARG TFM
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props Version.props Garnet.snk ./
COPY libs/storage/Tsavorite/cs/test/Directory.Build.props libs/storage/Tsavorite/cs/test/
COPY libs/storage/Tsavorite/cs/src/epoch/ libs/storage/Tsavorite/cs/src/epoch/
COPY libs/storage/Tsavorite/cs/test/epoch/litmus/ libs/storage/Tsavorite/cs/test/epoch/litmus/

RUN dotnet publish libs/storage/Tsavorite/cs/test/epoch/litmus/Garnet.LightEpoch.litmus.csproj \
        -c Release -f ${TFM} -o /app

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_RUNTIME_TAG}
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "/app/Garnet.LightEpoch.litmus.dll"]
