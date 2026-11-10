# Ручной запуск без Helm (манифесты Фазы 1)

Это оригинальные «голые» манифесты Фазы 1 (из коммита `8a47ee6`), чтобы можно было поднимать sandbox руками через
`kubectl apply` и видеть каждый объект целиком. Рабочий деплой проекта по-прежнему Helm — эти файлы ему не мешают:
всё живёт в **отдельном namespace `sandbox-manual`** (там нет mesh, default-deny NetworkPolicy и т.п.), образы —
`sandbox/*:0.1.0` (Helm использует `0.4.0`).

Отличия от оригинала: только namespace (`sandbox` → `sandbox-manual`). Без PostgreSQL: orders-api версии 0.1.0 хранит
заказы в памяти, поэтому образы собираются из старого коммита, а не из текущего `src/`.

## Запуск

```powershell
./infra/manual/build-images.ps1                 # образы 0.1.0 из коммита Фазы 1 + kind load
kubectl apply -f infra/manual/phase-1/00-namespace.yaml
kubectl create secret generic sandbox-api-key -n sandbox-manual --from-literal="api-key=$([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower())"
kubectl apply -f infra/manual/phase-1/
kubectl get pods -n sandbox-manual -w
```

Проверка (в Фазе 1 нет Ingress — port-forward):

```powershell
kubectl port-forward -n sandbox-manual svc/frontend 8080:80
curl.exe http://localhost:8080/
```

Убрать всё: `kubectl delete namespace sandbox-manual`.

Что изучать: [docs/10-phase-1-kubernetes-basics.md](../../docs/10-phase-1-kubernetes-basics.md) (там упражнения).
