FROM python:3.12-alpine
WORKDIR /app
COPY implementations/matrix/runtime/docker/verifier.py /app/verifier.py
ENTRYPOINT ["python", "/app/verifier.py"]
