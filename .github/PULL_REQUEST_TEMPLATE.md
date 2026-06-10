## Descripción

Describe brevemente el cambio y su motivación. Enlaza el issue relacionado si
aplica (ej. `Closes #123`).

## Tipo de cambio

- [ ] `fix` — corrección de error
- [ ] `feat` — nueva funcionalidad
- [ ] `refactor` — cambio interno sin alterar comportamiento
- [ ] `docs` — documentación
- [ ] `chore` / `test` — mantenimiento o pruebas

## Checklist

- [ ] `dotnet build Qora.Billing.sln` pasa sin errores.
- [ ] `dotnet test Qora.Billing.sln` pasa.
- [ ] `dotnet format --verify-no-changes` sin cambios pendientes.
- [ ] Si modifiqué el modelo EF Core, agregué la migración correspondiente.
- [ ] No incluí secretos, certificados ni datos tributarios reales.
- [ ] Actualicé la documentación si fue necesario.

## Notas para revisión

Cualquier detalle que el revisor deba tener en cuenta.
