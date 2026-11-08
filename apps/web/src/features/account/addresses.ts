import { apiGet, apiSend } from "@/lib/api";

export type Address = { id: string; label: string; recipientName: string; phone: string; province: string; district: string; streetAddress: string; postalCode: string | null; isDefault: boolean };
export type AddressInput = Omit<Address, "id" | "isDefault">;
// Adres yalnızca JSON gövdesinde taşınır; URL'ye ve depolamaya yazılmaz. Kimlik (uuid) yol parçasıdır, kişisel veri değildir.
export const listAddresses = () => apiGet<Address[]>("/addresses");
export const createAddress = (input: AddressInput) => apiSend<Address>("POST", "/addresses", input);
export const updateAddress = (id: string, input: AddressInput) => apiSend<Address>("PUT", `/addresses/${encodeURIComponent(id)}`, input);
export const setDefaultAddress = (id: string) => apiSend<Address>("POST", `/addresses/${encodeURIComponent(id)}/default`);
export const deleteAddress = (id: string) => apiSend<void>("DELETE", `/addresses/${encodeURIComponent(id)}`);
