FROM eclipse-temurin:21-jre

RUN curl -fL --retry 3 --connect-timeout 20 \
    https://github.com/tlaplus/tlaplus/releases/download/v1.7.4/tla2tools.jar \
    -o /opt/tla2tools.jar \
    && echo "936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88  /opt/tla2tools.jar" | sha256sum -c -
