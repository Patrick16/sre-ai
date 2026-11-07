# 11. Фаза 2: Ingress и TLS на практике

Теория — раздел «Ingress и Ingress Controller» и «TLS и mTLS» в [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Почему Traefik, а не ingress-nginx

Изначально план был на ingress-nginx — самый распространённый контроллер. Но в ноябре 2025
Kubernetes объявил о его закрытии, и с марта 2026 у ingress-nginx **нет релизов и исправлений
уязвимостей**. Существующие установки продолжают работать, но для нового «продового» кластера ставить
его нельзя. Рекомендация проекта — переходить на **Gateway API**
(источники: [объявление](https://www.kubernetes.io/blog/2025/11/11/ingress-nginx-retirement/),
[заявление Steering и Security Response Committees](https://www.kubernetes.io/blog/2026/01/29/ingress-nginx-statement/)).

Это хороший урок сам по себе: **инфраструктурный компонент может «умереть»**, и SRE должен следить за
жизненным циклом зависимостей, а не только за их аптаймом.

Выбрали **Traefik**: активно поддерживается, понимает и классический `Ingress`, и Gateway API.
Сначала учим Ingress (его всё ещё встретишь везде), потом на том же контроллере — Gateway API.

## Что развёрнуто

| Компонент | Как | Где |
|---|---|---|
| Traefik v3.7 (chart 42.3.1) | Helm | namespace `traefik`, [values](../infra/helm/values/traefik.yaml) |
| cert-manager v1.21.3 | Helm (OCI-чарт) | namespace `cert-manager`, [values](../infra/helm/values/cert-manager.yaml) |
| Свой CA: `selfsigned` → `sandbox-root-ca` → ClusterIssuer `sandbox-ca` | манифест | [01-cluster-issuers.yaml](../infra/manifests/phase-2/01-cluster-issuers.yaml) |
| Ingress `frontend` (`/`) и `orders-api` (`/api`), Middleware `strip-api` | манифест | [02-ingress.yaml](../infra/manifests/phase-2/02-ingress.yaml) |

```powershell
./scripts/deploy-phase2.ps1      # Helm-чарты → CA → Ingress, экспорт корневого CA в .local/
```

Инфраструктурные компоненты ставим **готовыми Helm-чартами сообщества** уже сейчас: писать их манифесты
руками бессмысленно. Свой чарт для наших сервисов — тема Фазы 3.

## Путь запроса

```
curl https://sandbox.localhost/        (*.localhost → 127.0.0.1, правка hosts не нужна)
  │
  ▼  порт 443 Windows
Docker: проброс 443 → контейнер-нода sre-ai-control-plane   (extraPortMappings в kind-config.yaml)
  │
  ▼  hostPort 443 на этой ноде
под Traefik  ── TLS терминируется здесь (сертификат из secret sandbox-tls)
  │  выбирает router по Host + Path: "/" → frontend, "/api" → orders-api (+ strip "/api")
  │  берёт адреса подов из EndpointSlices и ходит НАПРЯМУЮ в под, минуя ClusterIP
  ▼
под frontend 10.244.x.x:8080   (уже по обычному HTTP внутри кластера)
```

Важные детали:
- **У Ingress пустой `ADDRESS`** в `kubectl get ingress` — это нормально: у Traefik Service типа `ClusterIP`,
  балансировщика нет. В облаке здесь был бы IP облачного LoadBalancer.
- **TLS только до Traefik.** Внутри кластера трафик пока не шифруется. Это закроет mTLS в Фазе 6.
- **Traefik — единственный под с hostPort.** Отсюда `maxSurge: 0` в values: новый под не может занять порт,
  пока старый жив, поэтому при обновлении Traefik есть короткий простой. В проде контроллер работает в нескольких
  репликах за облачным балансировщиком, и этой проблемы нет.

## Как работает cert-manager

```
Ingress (annotation cert-manager.io/cluster-issuer: sandbox-ca, tls.secretName: sandbox-tls)
   └─► cert-manager создаёт Certificate sandbox-tls
          └─► CertificateRequest ─► ClusterIssuer sandbox-ca подписывает своим CA-ключом
                 └─► Secret sandbox-tls (tls.crt, tls.key, ca.crt) ─► Traefik подхватывает автоматически
```

Что получилось на деле (`kubectl get secret sandbox-tls -n sandbox`):
- `Issuer = CN=sre-ai sandbox root CA`, **`Subject` пустой** — имя хоста только в **SAN** (`DNS=sandbox.localhost`).
  Современные клиенты смотрят только на SAN; CN в серверных сертификатах устарел.
- Срок 90 дней, `renewalTime` — через 2/3 срока. cert-manager **сам продлит** сертификат. Ручное продление
  сертификатов — классическая причина реальных аварий («сертификат истёк в субботу ночью»).

Ingress `orders-api` использует **тот же** secret, но без аннотации cert-manager: иначе два Certificate боролись бы за один secret.

## Что мы обнаружили при проверке

### 1. Редирект HTTP → HTTPS
`curl http://sandbox.localhost/` → `301 → https://sandbox.localhost/`. Настроено на entrypoint `web` в values Traefik.

### 2. «Доверие» и «проверка отзыва» — разные вещи
`curl.exe --cacert .local/sandbox-root-ca.crt https://sandbox.localhost/` на Windows упал с
`schannel: the revocation status is unknown`. Цепочка доверия при этом в порядке: Schannel (TLS-стек Windows)
дополнительно пытается проверить, **не отозван ли** сертификат, а у нашего CA нет CRL/OCSP.
С `--ssl-no-revoke` — `200`. Без нашего CA — `SEC_E_UNTRUSTED_ROOT`, как и должно быть.
У публичных CA (Let's Encrypt) механизм отзыва есть; у внутренних CA его часто не делают — и вот как это проявляется.

### 3. Ingress балансирует по запросам, но только «на входе»
10 запросов к `/`: **5/5** между двумя подами frontend — в отличие от Фазы 1, где всё шло в один под.
Traefik — L7-прокси и выбирает под на каждый запрос. Но внутри:
```
5x  frontend-...-f75zn -> orders-api-...-t9c9j (1 orders)
5x  frontend-...-fg8mc -> orders-api-...-4q95p (2 orders)
```
Каждый под frontend по-прежнему держит keep-alive к «своему» поду orders-api. Ingress решает балансировку
только для внешнего трафика. Для трафика сервис→сервис нужен service mesh (Фаза 6).

### 4. Нет бэкендов → 503
`kubectl scale deploy/frontend --replicas=0` → Traefik отвечает `503 Service Unavailable`. Это отличный
сигнал для алертов (Фаза 5): рост 5xx на ingress — один из первых признаков проблемы.

## Браузер

Откройте https://sandbox.localhost/ — браузер предупредит, что сертификат не доверенный. Это правильно:
наш CA никто не знает. Варианты:
- просто принять предупреждение (для песочницы достаточно);
- **самостоятельно** добавить `.local/sandbox-root-ca.crt` в доверенные корневые сертификаты вашего пользователя
  (`certmgr.msc` → «Доверенные корневые центры сертификации» → Импорт). Это изменение настроек безопасности
  системы — делайте осознанно: любой, у кого окажется ключ этого CA (он лежит в secret кластера), сможет выпустить
  сертификат на любой сайт, которому поверит ваш браузер. После экспериментов удалите сертификат из хранилища.
  Firefox использует собственное хранилище сертификатов.

## Дашборд Traefik

```powershell
kubectl port-forward -n traefik deploy/traefik 9000:8080
# http://localhost:9000/dashboard/
```
Виден каждый router (правило Host/Path), его middleware и бэкенд-сервис. Дашборд опубликован только на внутреннем
entrypoint `traefik` — наружу он не выставлен (выставлять его без аутентификации нельзя).

## Ingress vs Gateway API — почему это важно

Обратите внимание на `traefik.ingress.kubernetes.io/router.middlewares: sandbox-strip-api@kubernetescrd`.
Срезать префикс пути спецификация Ingress **не умеет** — каждый контроллер делает это своими аннотациями и CRD.
Переехать с одного контроллера на другой = переписать все аннотации. Именно эту проблему решает **Gateway API**:
`Gateway` (что слушаем, чей контроллер) и `HTTPRoute` (маршруты, переписывание путей, заголовков, разделение
трафика) — стандартные объекты, одинаковые для всех контроллеров. Упражнение 7 ниже.

## Упражнения

| # | Сделать | Что ожидать |
|---|---|---|
| 1 | `kubectl describe certificate sandbox-tls -n sandbox`, затем `kubectl get certificaterequest -n sandbox` | Цепочка объектов cert-manager и их статусы |
| 2 | `kubectl delete secret sandbox-tls -n sandbox`, через 10 с снова `get secret` | cert-manager перевыпустил сертификат сам (новый serial) |
| 3 | Скопировать Certificate с `duration: 1h`, `renewBefore: 50m` для нового хоста и понаблюдать | Продление каждые ~10 минут — так тестируют ротацию |
| 4 | Добавить host-based маршрут: `api.sandbox.localhost` → orders-api без strip, добавить хост в `tls.hosts` | cert-manager перевыпустит сертификат с двумя SAN |
| 5 | `kubectl delete pod -n traefik -l app.kubernetes.io/name=traefik` и сразу curl в цикле | Короткий простой: Traefik — единственная точка входа (SPOF) |
| 6 | Поставить в Ingress несуществующий `ingressClassName: nginx` | Traefik игнорирует Ingress → 404. Частая ошибка при миграции |
| 7 | Включить `providers.kubernetesGateway.enabled: true` в values и описать тот же маршрут через `Gateway` + `HTTPRoute` (с `URLRewrite` вместо Middleware) | Тот же результат стандартными объектами, без аннотаций Traefik |
| 8 | Смотреть `kubectl logs -n traefik deploy/traefik -f` во время запросов | Access log: router, адрес пода-бэкенда, время ответа |

## Шпаргалка отладки

| Симптом | Где смотреть | Частая причина |
|---|---|---|
| `404 page not found` от Traefik | Дашборд → routers | Нет подходящего router: хост/путь/`ingressClassName` не совпадают |
| `503` | `kubectl get endpointslices -n sandbox` | У Service нет готовых подов |
| `502`/`504` | Access log Traefik, логи пода | Под отвечает ошибкой / не отвечает вовремя |
| Сертификат «не тот» / дефолтный Traefik | `kubectl get certificate -A` | Certificate не Ready, secret не создан, хост не в `tls.hosts` |
| Certificate застрял в `False` | `kubectl describe certificaterequest`, логи `cert-manager` | Issuer не Ready, опечатка в имени issuer |
| `curl: (60) revocation status is unknown` (Windows) | — | Нет CRL у внутреннего CA → `--ssl-no-revoke` |
| `ERR_CONNECTION_REFUSED` на 80/443 | `docker ps` (проброс портов ноды) | Кластер создан без `extraPortMappings` или порт занят на хосте |
