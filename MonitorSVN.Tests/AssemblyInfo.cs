using Xunit;

// AppLog e estatico (pasta e arquivo do dia) e quase todo servico grava nele. Com as classes de teste em paralelo,
// as linhas de uma caem no log da outra e os testes de log falham de vez em quando. A suite roda em poucos segundos.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
