# Web client

## Contexto

O Ouroboros não tem interface gráfica: o sistema só é exercitado por HTTP, testes e comandos. Para apresentar o projeto, é preciso um cliente que qualquer pessoa abra por um link.

O risco de colocar uma interface no monorepo é ela virar atalho para o que a migração para microsserviços removeu: referência a assemblies dos serviços, leitura de banco alheio, publicação direta no broker ou regras de negócio duplicadas. Esta decisão define o web client como **consumidor externo dos serviços**, sujeito às mesmas fronteiras de qualquer cliente fora do sistema.

A disponibilidade do projeto está no [estado de implementação](../implementation-status.md).

## Decisão

### Papel

- O web client é um **cliente de apresentação** com papel de BFF (Backend for Frontend), em `src/Clients/Ouroboros.WebClient`, fora de `src/Services`.
- **Não é serviço:** não possui domínio, regras de negócio, banco de negócio, migrations nem contratos de integração próprios.
- **Público inicial:** somente `Customer`, pelo gateway externo. O backoffice (`Admin`) fica fora do web client. Atendê-lo no mesmo processo exigiria expor o [`AdminGateway`](hosts.md#gateways), restrito a loopback por padrão, a um executável voltado à internet; um cliente administrativo será decisão própria.

### Fronteira com os microsserviços

O web client **nunca ultrapassa as fronteiras dos serviços**. Ele se comporta como um navegador ou um integrador externo, com a diferença de guardar os tokens no servidor:

| Permitido | Proibido |
| --- | --- |
| HTTP para gateways, por uma única URL base configurável (inicialmente [`Ouroboros.ExternalGateway.Api`](../../src/Gateways/Ouroboros.ExternalGateway.Api)) | URL direta de serviço, inclusive em desenvolvimento |
| Referência ao projeto `Ouroboros.Observability` | Referência a projetos de `src/Services`, `Ouroboros.BuildingBlocks`, `Ouroboros.Messaging` ou `Ouroboros.Hosting` |
| Pacotes de UI, HTTP e autenticação por cookie | EF Core, Npgsql, RabbitMQ.Client e qualquer `ConnectionStrings` |
| Modelos HTTP próprios, espelhando os contratos públicos | `Contracts` dos serviços: são fronteiras de DI internas do proprietário ([estrutura](module-structure.md#visibilidade-dos-tipos)), não schemas HTTP |
| Identificadores públicos (`ExternalId`) | `Id` interno, entidades ou detalhes de persistência |

- **Sem regra de negócio.** Validação na interface é conveniência de usabilidade. A resposta autoritativa é a do serviço, inclusive o [ProblemDetails](error-responses.md) de recusa.
- **Sem decisão de autorização.** O token é opaco para o web client: ele não valida JWT nem infere permissões dos claims. Mostrar ou esconder um menu é apresentação; a autorização acontece no serviço proprietário em toda chamada ([autenticação](authentication.md#autorização)).
- **Indisponibilidade.** `502` e `504` do gateway aparecem como serviço indisponível, com o `traceId` para suporte. O web client não repete chamadas que alteram estado.
- **Correlação.** As chamadas aos gateways levam o `traceparent` da requisição ([W3C Trace Context](trace-correlation.md)), ligando o clique na interface aos logs dos gateways e dos serviços.

### Tecnologia

- **Blazor Web App com render mode Interactive Server global.** A interface roda no servidor, sobre um circuito SignalR; nenhum código de aplicação nem token vai para o navegador.
- **Login e logout em SSR estático**, por formulário e endpoint. O cookie de sessão só pode ser emitido numa resposta HTTP, e o circuito interativo não tem uma.
- **MudBlazor** como biblioteca de componentes (linha 9.x, compatível com .NET 10), com versão centralizada em `Directory.Packages.props` ([pacotes](module-structure.md#pacotes-nuget)).
- **Identidade visual** ([#124](https://github.com/adnerscarpelini/ouroboros-server/issues/124)): ver [Identidade visual](#identidade-visual).

### Sessão (BFF)

- O login chama `POST /api/v1/identity/sessions` pelo gateway e recebe o access token e o refresh token ([login](../identity/login.md)). Os dois ficam num **store em memória do servidor**, indexado pela sessão do web client.
- O navegador recebe apenas um **cookie HttpOnly, Secure e SameSite** da autenticação do próprio web client. Tokens nunca aparecem em cookie legível, storage do navegador ou HTML.
- A renovação usa `POST /api/v1/identity/sessions/refresh` ([renovação](../identity/refresh-session.md)) quando o access token está para expirar, conforme o `accessTokenExpiresAt` devolvido; refresh token recusado encerra a sessão local e leva ao login.
- O logout chama `POST /api/v1/identity/sessions/logout` ([logout](../identity/logout.md)) e limpa o cookie e o store. O access token já emitido vale até expirar, conforme o contrato de Identity.
- As chaves de Data Protection, que protegem o cookie, ficam em volume próprio no container, para o cookie continuar válido entre reinícios do processo.

### Estrutura interna

Um único projeto, organizado por funcionalidade, com as pastas de `Features` nomeadas pelos serviços do backend. Quem conhece `src/Services/Identity` encontra as telas em `Features/Identity`.

```text
src/Clients/Ouroboros.WebClient/
  Program.cs              # ponto de entrada; a composição fica em WebClientHost.cs
  WebClientHost.cs        # composição: infraestrutura, Add<Serviço>Feature(), MudBlazor e pipeline
  _Imports.razor          # usings dos componentes, válidos em todas as pastas
  Components/             # App.razor e Routes.razor, exigidos pelo Blazor
  Shell/                  # layout, navegação, rodapé, tema, página inicial (Landing/), erros e reconexão
  Shared/                 # componentes genéricos, sem serviço dono
  Infrastructure/         # pipeline HTTP ao gateway, sessão BFF, tradução de ProblemDetails e opções
  Features/
    Identity/
      Pages/              # telas roteáveis
      Components/         # componentes usados só por Identity
      Client/             # HttpClient tipado para /api/v1/identity via gateway
      Models/             # modelos HTTP próprios
      IdentityFeature.cs  # registro de DI de Identity no web client
```

| Origem | Pode usar | Não pode usar |
| --- | --- | --- |
| `Features/<Serviço>` | O próprio serviço, `Shared`, `Infrastructure` e `Shell` (layout) | Outro `Features/<Serviço>`, `Components` e a raiz (a composição usa todas as áreas e viraria ponte entre `Features`) |
| `Shared` | `Shared` e `Infrastructure` | `Features`, `Shell` e `Components` |
| `Infrastructure` | A própria `Infrastructure`, bibliotecas HTTP e de autenticação, `Ouroboros.Observability` | `Features`, `Shared`, `Shell` e `Components` |
| `Shell` | `Shared`, `Infrastructure`, `Components` e rotas em texto para o menu | Tipos de `Features` |
| `Components` | Todas as áreas (`App` e `Routes` montam o roteador e o layout) | Nenhuma restrição de área |
| `WebClientHost.cs` e `Program.cs` | `Add<Serviço>Feature()` e a infraestrutura | Lógica de tela |

Não existem outras pastas de código: um namespace fora dessas áreas é recusado, para que uma pasta nova não sirva de ponte entre `Features`. A área vem do namespace, então o namespace de cada arquivo C# é o da sua pasta, e os componentes Razor não usam `@namespace`.

- **`Features`, não `Domains`.** O web client não tem camada de domínio, e `Domain` já designa a camada de regras dentro de cada serviço (`Ouroboros.Identity.Domain`). Organizar por funcionalidade é o padrão de *feature folders*.
- **Pasta só para serviço existente.** Uma pasta em `Features` existe quando o serviço expõe HTTP pelo gateway e tem telas. Hoje, apenas Identity; Notifications não tem API de negócio. Pastas de Customers ou da fronteira financeira só surgem quando esses serviços existirem, como na [criação de serviços](module-structure.md#criação-de-novos-serviços).
- **Isolamento entre serviços.** Uma tela usa um único serviço, como `type.cross-service` nos serviços ([dependências](layer-dependencies.md#regras-de-tipo)). Telas que combinem dados de mais de um serviço não são permitidas até uma decisão própria (área de composição explícita ou agregação no gateway).
- **Rotas com prefixo do serviço** (`/identity/...`), mostrando o dono da tela como as rotas da API (`/api/v1/identity/...`).
- **Visibilidade.** Clientes HTTP, modelos e serviços do web client são `internal` por padrão. Componentes Razor são `public` porque o compilador os gera assim; é a exceção documentada. O marcador `WebClientEntryPoint` também é público, só para os testes (`WebApplicationFactory`), como nos gateways.
- **Pastas criadas sob demanda.** `Shared` e as subpastas de `Features/Identity` (`Pages`, `Client`, `Models`) surgem com o primeiro componente ou tela que as use; hoje `Features/Identity` tem apenas o ponto de composição `AddIdentityFeature()`.

### Identidade visual

A [#124](https://github.com/adnerscarpelini/ouroboros-server/issues/124) define a aparência da vitrine do projeto: navegação no topo, tema escuro como padrão e a página inicial como apresentação da arquitetura.

- **Tokens.** Cores, superfícies, bordas, raios, sombras e durações são custom properties em [`wwwroot/app.css`](../../src/Clients/Ouroboros.WebClient/wwwroot/app.css), com um conjunto para `[data-theme="dark"]` (padrão) e outro para `[data-theme="light"]`. [`OuroborosTheme`](../../src/Clients/Ouroboros.WebClient/Shell/OuroborosTheme.cs) repete os valores na paleta MudBlazor, para que menus e popovers da biblioteca sigam a mesma identidade.
- **Acento `#ff6200` e contraste (WCAG 2.2 AA).** Texto branco sobre esse laranja fica em torno de 3:1, abaixo dos 4,5:1 exigidos para texto normal; por isso os botões de acento usam texto quase preto (cerca de 6,6:1). O acento em texto usa `#ff7a26` no tema escuro (cerca de 7,6:1) e `#c2410c` no claro (cerca de 5:1 sobre `#fafafa`); sobre fundos tingidos de laranja, como o selo do destaque, o tema claro usa `#9a3412`. `#ff6200` como texto aparece só em elementos grandes ou decorativos, como o ponto da marca e o início do gradiente do título.
- **Foco visível.** Todo link e botão mostra contorno de 2 px no acento (`#ff6200` no escuro, `#c2410c` no claro, ambos acima de 3:1). A regra tem especificidade maior que o `outline: none` do MudBlazor, inclusive nos itens do menu compacto.
- **Tipografia.** Inter variável servida pelo próprio web client ([`wwwroot/fonts`](../../src/Clients/Ouroboros.WebClient/wwwroot/fonts), licença SIL OFL 1.1 junto do arquivo), com títulos em tamanho fluido (`clamp()`). Nenhuma fonte vem de CDN em tempo de execução.
- **Tema.** [`js/ouroboros.js`](../../src/Clients/Ouroboros.WebClient/wwwroot/js/ouroboros.js) é carregado no `<head>` e aplica o tema antes da primeira pintura: a escolha salva no `localStorage` do navegador ou, na primeira visita, `prefers-color-scheme`. O botão claro/escuro grava a escolha, e a cor da barra do navegador (`theme-color`) acompanha o tema; não há cookie nem estado no servidor.
- **Layout.** Navegação fixa e translúcida no topo, com menu compacto (MudBlazor) abaixo de 768 px; não há menu lateral enquanto não existir área logada. Página inicial em [`Shell/Landing`](../../src/Clients/Ouroboros.WebClient/Shell/Landing): destaque com o cartão de um trace de cadastro, fluxo da arquitetura, *bento grid* dos pilares e chamada final. Os textos descrevem apenas fatos documentados, sem métricas que envelheçam.
- **Movimento.** A entrada das seções ao rolar usa animações guiadas pela rolagem em CSS (`animation-timeline: view()`), sem JavaScript: o conteúdo nunca depende do circuito Blazor para aparecer, e navegadores sem suporte mostram tudo sem animação. Com `prefers-reduced-motion: reduce`, animações, atrasos e transições são desligados. O destaque do topo não tem animação de entrada.
- **Logo.** [`img/logo-mark.png`](../../src/Clients/Ouroboros.WebClient/wwwroot/img/logo-mark.png) e o favicon derivam do `logo.png` da raiz, com o branco removido por *color to alpha* na resolução original (sem halo nas bordas), para funcionar nos dois temas.

### Execução, configuração e saúde

- Configuração na seção `WebClient`: `Gateway:BaseUrl` (origem do gateway, sem credenciais, caminho, query ou fragmento), `Gateway:Timeout` (até 30 segundos) e `DataProtection:KeysPath`. Valores inválidos, ou qualquer `ConnectionStrings`, impedem a subida com `OptionsValidationException` ([`WebClientOptionsValidator`](../../src/Clients/Ouroboros.WebClient/Infrastructure/WebClientOptionsValidator.cs)).
- Um único `HttpClient` nomeado (`gateway`) aponta para essa origem; o `traceparent` da requisição segue nas chamadas.
- `/health/live` responde sem consultar o gateway; `/health/ready` e `/health` consultam o `/health/ready` do gateway, com prazo de 2 segundos, e respondem apenas `{"status":"Healthy"}` ou `{"status":"Unhealthy"}`, como os serviços. O web client não referencia `Ouroboros.Hosting`, que reúne mecanismos das APIs dos serviços, e mantém a própria versão desses endpoints ([`WebClientHealthEndpoints`](../../src/Clients/Ouroboros.WebClient/Infrastructure/WebClientHealthEndpoints.cs)).
- Porta `5161` no desenvolvimento e no container; no Compose, profile `webclient` com volume `web-client-keys` para as chaves de Data Protection ([operação](../development.md#executar-o-web-client)).

### Verificação

As regras desta decisão são verificadas pelos testes de arquitetura ([dependências](layer-dependencies.md#web-client)):

| Garantia | Regra ou teste |
| --- | --- |
| Projeto em `src/Clients` referencia só `Ouroboros.Observability`, e nenhum projeto o referencia | `project.client-reference`, e `project.service-depends-on-executable`, `project.shared-depends-on-service` ou `project.executable-depends-on-executable` para quem o referenciar |
| Sem pacotes de banco ou broker (EF Core, Npgsql, RabbitMQ.Client, SqlClient) | `project.client-package` |
| Nenhum tipo de serviço, executável, banco ou broker usado no código | `type.client-backend-dependency` |
| Um `Features/<Serviço>` não usa outro | `type.client-cross-feature` |
| `Shared`, `Infrastructure` e `Shell` respeitam a tabela de pastas | `type.client-area` |
| Nenhum código fora das áreas conhecidas; namespace igual à pasta, sem `@namespace` nos componentes | `type.client-unknown-area` e `Web_client_namespaces_match_their_folders` |
| Subida recusada com gateway inválido ou connection strings; health sanitizado; Dockerfile não-root versionado; Compose sem banco, broker ou serviços; imagem na CI | [`WebClientArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/WebClientArchitectureTests.cs) |

## Alternativas consideradas

| Alternativa | Motivo de não adotar |
| --- | --- |
| WPF | Só Windows e exige instalar o executável e ter os serviços acessíveis: inadequado para apresentação por link |
| .NET MAUI | Mesmo problema de distribuição; só se justificaria com objetivo mobile |
| Blazor WebAssembly ou SPA JavaScript | Tokens ou chamadas no navegador exigiriam um BFF de qualquer forma; SPA JavaScript foge da stack .NET do projeto |
| Fluent UI Blazor | Mantida pela Microsoft, mas a v5 foi reescrita recentemente, com migração a partir da v4; MudBlazor oferece catálogo maior (DataGrid, Charts) em linha estável |
| Tokens no cookie cifrado | Sobrevive a reinícios, mas a renovação acontece dentro do circuito interativo, que não consegue reescrever o cookie |
| Um projeto por serviço (Razor Class Library) | Complexidade sem ganho num cliente único; os serviços também organizam camadas como pastas de um projeto |
| Web client atendendo Customer e Admin | Expõe o público administrativo num executável voltado à internet, contrariando a restrição de loopback do `AdminGateway` |

## Consequências

- O web client depende apenas dos contratos HTTP públicos: uma mudança de contrato em um serviço afeta o web client como afetaria qualquer consumidor externo, e não por compilação.
- Os modelos HTTP são duplicados no cliente. É o custo aceito para não acoplar o web client aos assemblies dos serviços.
- Reiniciar o web client encerra as sessões, porque o store de tokens é em memória. Aceito para um projeto de estudo; várias réplicas exigiriam store distribuído.
- O circuito Interactive Server mantém estado por usuário no servidor e depende da conexão SignalR; queda da conexão interrompe a interação até reconectar.
- As fronteiras desta decisão são verificadas por testes de arquitetura e por validação de opções na subida desde a criação do projeto ([#122](https://github.com/adnerscarpelini/ouroboros-server/issues/122)); ver [Verificação](#verificação).
- A preferência de tema fica só no navegador de cada visitante; ela não acompanha a pessoa entre dispositivos.

## Referências

- [Microsoft: cenários adicionais de segurança em Blazor](https://learn.microsoft.com/aspnet/core/blazor/security/additional-scenarios): tokens mantidos no servidor e obtidos no SSR estático para uso na renderização interativa.
- [Microsoft: Blazor Web App com OpenID Connect](https://learn.microsoft.com/aspnet/core/blazor/security/blazor-web-app-with-oidc): variantes com e sem o padrão BFF.
- [Duende: BFF e Blazor](https://docs.duendesoftware.com/bff/fundamentals/blazor/): modelo em que os tokens não saem do servidor; o produto não é adotado.
- [MudBlazor](https://github.com/MudBlazor/MudBlazor) e [Fluent UI Blazor](https://github.com/microsoft/fluentui-blazor).

## Origem

[#121](https://github.com/adnerscarpelini/ouroboros-server/issues/121) (decisão de arquitetura e fronteiras do web client), [#122](https://github.com/adnerscarpelini/ouroboros-server/issues/122) (projeto, health, imagem, Compose e regras de arquitetura) e [#124](https://github.com/adnerscarpelini/ouroboros-server/issues/124) (identidade visual).

[Voltar à arquitetura](README.md)
