# KubernetesPool Interactive Authentication - Incremental Changelog

- Added an opt-in matrix-host authentication mode that keeps existing matrix diagnostics and bootstrap services while using the normal standalone JWT authentication boundary.
- Preserved the historical matrix static bearer scheme as the default when the new option is disabled.
- Allowed the standalone access-context endpoint to be mapped when matrix diagnostics are enabled with standalone authentication.
- Preserved normal access-context rotation behavior for standalone-authentication matrix hosts while retaining the historical fixed-context rotation policy for static-bearer matrix hosts.
- Added integration coverage proving that historical matrix hosts continue to select `MatrixBearer` and opt-in standalone-authentication matrix hosts select the standard JWT bearer scheme without registering `MatrixBearer`.
- No Kubernetes lifecycle, scheduler, queue, DAG, recovery, replay, result-acceptance, persistence, or external SDK contract authority was changed.
