-- Smoke test to verify busted infrastructure works
describe("busted infrastructure", function()
  it("runs tests", function()
    assert.is_true(true)
  end)

  it("has vim mock available", function()
    assert.is_not_nil(vim)
    assert.is_not_nil(vim.split)
  end)
end)
