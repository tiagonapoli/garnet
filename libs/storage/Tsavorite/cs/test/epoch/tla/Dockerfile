# TLC model checker for the CAS-carries-the-announce LightEpoch fix.
#
# Build (from the repository root):
#   docker build -t garnet-lightepoch-tla libs/storage/Tsavorite/cs/test/epoch/tla
# Run every spec:
#   docker run --rm garnet-lightepoch-tla
#
# Exits non-zero if any spec result does not match its expectation.

FROM eclipse-temurin:21-jre

ADD https://github.com/tlaplus/tlaplus/releases/latest/download/tla2tools.jar /opt/tla2tools.jar

WORKDIR /work
COPY . .
RUN sed -i 's/\r$//' run.sh && chmod +x run.sh

ENTRYPOINT ["bash", "run.sh"]
