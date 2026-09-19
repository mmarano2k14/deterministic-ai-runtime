def run(inputs, context):
    return {
        "success": True,
        "payload": {
            "workerLanguage": "python",
            "marker": inputs.get("marker"),
        },
    }
