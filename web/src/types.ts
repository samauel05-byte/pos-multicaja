export type Role = 'Admin' | 'Supervisor' | 'Cashier'
export interface Me { id: string; username: string; fullName: string; role: Role; permissions: string[] }
export interface Company { name: string; taxId?: string; currencyCode: string; currencySymbol: string; pricesIncludeTax: boolean; allowNegativeStock: boolean
  cashierMaxDiscountPercent: number; supervisorMaxDiscountPercent: number; cashDifferenceToleranceAmount: number; ticketFooter?: string; fiscalModuleEnabled: boolean }
export interface Register { id: string; name: string; branchId: string; locationId: string }
export interface Bootstrap {
  company: Company
  branches: { id: string; name: string }[]
  locations: { id: string; name: string; isDefault: boolean }[]
  registers: Register[]
  categories: { id: string; name: string }[]
  units: { id: string; name: string; abbreviation: string; allowsDecimals: boolean }[]
  taxRates: { id: string; name: string; rate: number }[]
  openShifts: { id: string; registerId: string; openedByUserId: string; openedAt: string; openingFloat: number }[]
}
export interface Product { id: string; sku: string; barcode?: string; name: string; categoryId?: string; categoryName?: string; unitId: string; unitAbbr: string; allowsDecimals: boolean
  taxRateId: string; taxRate: number; cost?: number | null; price: number; minStock: number; tracksStock: boolean; active: boolean; stock: number }
export interface Customer { id: string; name: string; taxId?: string; phone?: string; email?: string; creditLimit: number; creditDays: number; balance: number; available: number; active: boolean }
export interface ShiftSummary { shiftId: string; registerId: string; registerName: string; openedBy: string; openedAt: string; closedAt?: string; status: string
  openingFloat: number; cashSales: number; cashIn: number; cashOut: number; cashRefunds: number; cashPayments: number; expectedCash: number
  cardSales: number; transferSales: number; creditSales: number; salesCount: number; salesTotal: number; voidedCount: number
  countedCash?: number; cashDifference?: number }
export interface SaleLine { id: string; productId: string; productName: string; sku: string; quantity: number; unitPrice: number; discountPercent: number; discountAmount: number
  taxRate: number; netAmount: number; taxAmount: number; lineTotal: number; returnedQuantity: number }
export interface Sale { id: string; number: string; createdAt: string; status: 'Completed' | 'Voided'; subtotal: number; discountTotal: number; taxTotal: number; total: number
  creditAmount: number; creditBalance: number; changeGiven: number; currencySymbol: string; customerName?: string; cashierName: string; registerName: string; registerId: string
  printCount: number; voidReason?: string; lines: SaleLine[]; payments: { method: string; amount: number; tendered: number; reference?: string }[] }
export interface Ticket { text: string; escPosBase64: string; isCopy: boolean; printCount: number; printJobId: string }
