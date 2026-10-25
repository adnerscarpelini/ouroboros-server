extern alias identityapi;

// O WebApplicationFactory da API de Identity, sem repetir o alias do assembly em cada teste.
global using ApiFactory = Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint>;
