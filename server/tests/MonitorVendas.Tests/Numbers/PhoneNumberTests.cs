using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Tests.Numbers;

public class PhoneNumberTests
{
    // O caso real que motivou a normalização: o cadastro antigo tem 11 dígitos
    // (sem DDI) e o JID do WhatsApp tem 13. Comparar como string colocaria um
    // número legítimo em quarentena.
    [Fact]
    public void SameNumber_IgnoresTheCountryCode()
    {
        Assert.True(PhoneNumber.SameNumber("11968608425", "5511968608425"));
        Assert.True(PhoneNumber.SameNumber("5511968608425", "5511968608425"));
    }

    // Celular antigo circula sem o 9º dígito; é o mesmo aparelho.
    [Fact]
    public void SameNumber_IgnoresTheNinthDigit()
    {
        Assert.True(PhoneNumber.SameNumber("5511968608425", "551168608425"));
        Assert.True(PhoneNumber.SameNumber("11968608425", "1168608425"));
    }

    // Números diferentes continuam diferentes — inclusive com DDD trocado, que é
    // o erro que uma comparação por sufixo deixaria passar.
    [Fact]
    public void SameNumber_TellsDifferentNumbersApart()
    {
        Assert.False(PhoneNumber.SameNumber("5511968608425", "5511968608426"));
        Assert.False(PhoneNumber.SameNumber("5511968608425", "5521968608425"));
        Assert.False(PhoneNumber.SameNumber("", "5511968608425"));
    }

    // O JID vem com sufixo de dispositivo em alguns eventos.
    [Fact]
    public void FromJid_ExtractsTheNumber()
    {
        Assert.Equal("5511968608425", PhoneNumber.FromJid("5511968608425@s.whatsapp.net"));
        Assert.Equal("5511968608425", PhoneNumber.FromJid("5511968608425:12@s.whatsapp.net"));
        Assert.Equal(string.Empty, PhoneNumber.FromJid(null));
    }

    // Guardar sempre com DDI: é o que torna o cadastro comparável com o JID.
    [Fact]
    public void ToStorage_AddsTheCountryCodeWhenItIsMissing()
    {
        Assert.Equal("5511968608425", PhoneNumber.ToStorage("11968608425"));
        Assert.Equal("5511968608425", PhoneNumber.ToStorage("+55 (11) 96860-8425"));
        Assert.Equal("5511968608425", PhoneNumber.ToStorage("5511968608425"));
    }

    // Formato de exibição pedido: +55 11 91234-4567.
    [Fact]
    public void Format_UsesTheBrazilianLayout()
    {
        Assert.Equal("+55 11 91234-4567", PhoneNumber.Format("5511912344567"));
        Assert.Equal("+55 11 91234-4567", PhoneNumber.Format("11912344567"));
        Assert.Equal("+55 11 1234-4567", PhoneNumber.Format("551112344567"));
    }

    // O que não cabe no formato brasileiro (DDD + 8 ou 9 dígitos, com ou sem o
    // 55) sai só com os dígitos: inventar formato seria pior que não formatar.
    [Fact]
    public void Format_LeavesUnknownShapesAlone()
    {
        Assert.Equal("442071234567", PhoneNumber.Format("+44 20 7123 4567"));
        Assert.Equal("12345", PhoneNumber.Format("12345"));
        Assert.Equal(string.Empty, PhoneNumber.Format(null));
    }
}
