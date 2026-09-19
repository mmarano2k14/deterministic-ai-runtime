ARG BASE_IMAGE
FROM ${BASE_IMAGE}

USER root
COPY implementations/python/workers/hosted_invocation/worker.py /opt/multiplexed/hosted_invocation/worker.py
RUN chmod 0555 /opt/multiplexed/hosted_invocation/worker.py

USER 65532:65532
WORKDIR /tmp
ENTRYPOINT ["python3", "-I", "-S", "-B", "-u", "-X", "utf8", "/opt/multiplexed/hosted_invocation/worker.py"]
