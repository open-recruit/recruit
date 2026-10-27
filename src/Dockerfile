FROM docker.io/library/gradle:9.8.2-jdk25@sha256:ca424744a6a45a575f333edea680696d980999c461be38b1574a4c70d5455f24 AS build
SHELL ["/bin/bash", "-eo", "pipefail", "-c"]
WORKDIR /home/gradle/project

COPY . .

ARG MODULE_NAME=query-sql-on-fhir
# all of caches/, not just the downloaded dependencies in caches/modules-2: the build cache and the
# instrumented plugin jars live there too, and would otherwise be rebuilt by every build.
RUN --mount=type=cache,target=/home/gradle/.gradle/caches <<EOF
gradle clean ":${MODULE_NAME}:build" -x test --info
java -Djarmode=tools -jar ${MODULE_NAME}/build/libs/*.jar extract --layers --launcher --force \
    --destination "${MODULE_NAME}/"
EOF

FROM build AS test
ENV TZ="UTC"
RUN --mount=type=cache,target=/home/gradle/.gradle/caches <<EOF
gradle ":${MODULE_NAME}:test"
gradle ":${MODULE_NAME}:jacocoTestReport"
EOF

FROM gcr.io/distroless/java25-debian13:nonroot@sha256:5d3177252775d4b01603470fe21c015393019089904f68724bd37242e4bc6e6d
WORKDIR /app

ARG MODULE_NAME=query-sql-on-fhir
COPY --from=build /home/gradle/project/${MODULE_NAME}/dependencies/ ./
COPY --from=build /home/gradle/project/${MODULE_NAME}/spring-boot-loader/ ./
COPY --from=build /home/gradle/project/${MODULE_NAME}/snapshot-dependencies/ ./
COPY --from=build /home/gradle/project/${MODULE_NAME}/application/ ./

USER 65532:65532
ENV SPRING_PROFILES_ACTIVE="prod"
ENTRYPOINT ["java", "org.springframework.boot.loader.launch.JarLauncher"]
