FROM python:3.12-alpine
WORKDIR /app
COPY implementations/matrix/runtime/docker/verifier.py /app/verifier.py
COPY implementations/matrix/multilanguage-runtime-matrix-v1.json /app/matrix-plan.json
ENTRYPOINT ["python", "/app/verifier.py"]
